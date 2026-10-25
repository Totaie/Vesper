namespace Vesper.Core.Rfb;

/// <summary>
/// Which sign-in a connection may use, from what the path proves about the far end and what
/// Vesper already knows about the Mac.
///
/// Apple's RSA sign-in (type 33) proves the Mac's identity once its first encrypted record checks
/// out. Apple's Diffie-Hellman sign-in (type 30) proves nothing about who answered, so it's only
/// used where the path itself does (an SSH tunnel with a pinned host key, or straight to a
/// Tailscale node the user named), or for a Mac the user explicitly allowed it for, and never,
/// outside SSH, for a Mac whose identity key Vesper already knows.
/// </summary>
public sealed record SignInPolicy(AuthPolicy Auth, bool RetryWithDiffieHellman, bool RememberRsaRefusal, bool AllowUnencrypted)
{
    /// <param name="insideSsh">The session runs inside an SSH tunnel whose host key is pinned.</param>
    /// <param name="pathAuthenticatesMac">The connection goes straight to an address the network authenticates (a Tailscale node).</param>
    /// <param name="allowLegacySignIn">The user allowed Diffie-Hellman for this Mac on networks Vesper can't vouch for.</param>
    /// <param name="identityPinned">This Mac has proven its RSA identity key before.</param>
    /// <param name="rsaRefusedBefore">This Mac refused RSA sign-in over an authenticated path before, and took Diffie-Hellman.</param>
    public static SignInPolicy Decide(bool insideSsh, bool pathAuthenticatesMac, bool allowLegacySignIn, bool identityPinned, bool rsaRefusedBefore)
    {
        bool authenticated = insideSsh || pathAuthenticatesMac;

        // A Mac that has proven its identity key and then stops offering that sign-in looks
        // exactly like an attacker stripping it. Inside SSH the pinned host key still vouches for
        // the Mac; a Tailscale address only proves which tailnet node answered, and any node on
        // the tailnet could answer for the Mac's name. So once the key is known, only it counts.
        bool diffieHellman = insideSsh || (!identityPinned && (pathAuthenticatesMac || allowLegacySignIn));
        var auth = !diffieHellman ? AuthPolicy.RsaOnly
            : authenticated && rsaRefusedBefore ? new AuthPolicy(AllowRsa: false, AllowDiffieHellman: true)
            : AuthPolicy.Any;

        return new SignInPolicy(
            auth,
            // A Mac reports a refused RSA login exactly like a wrong password. Where Diffie-Hellman
            // is allowed anyway, trying it once costs nothing in security.
            RetryWithDiffieHellman: diffieHellman && auth.AllowRsa,
            // Only an authenticated path makes the refusal trustworthy enough to remember; on an
            // open network anyone could have sent it.
            RememberRsaRefusal: authenticated,
            // Without Apple's record layer only the SSH tunnel protects the session.
            AllowUnencrypted: insideSsh);
    }
}
