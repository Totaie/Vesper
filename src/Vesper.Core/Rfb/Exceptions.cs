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
}
