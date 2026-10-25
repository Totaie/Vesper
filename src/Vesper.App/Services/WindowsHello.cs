using System.Runtime.InteropServices.WindowsRuntime;
using Vesper.Interop;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace Vesper.Services;

/// <summary>
/// A Windows Hello key used only to unlock Vesper's vault. Its RSA signature over a random salt
/// (PKCS#1 v1.5, so repeatable) is fed through HKDF to make the vault's key-encryption key; the
/// private key never leaves the TPM, and every use asks for your face, fingerprint or PIN.
/// </summary>
internal sealed class WindowsHello : IUserPresence
{
    private const string CredentialName = "Vesper password vault";

    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            return await KeyCredentialManager.IsSupportedAsync();
        }
        catch
        {
            return false;
        }
    }

    public async Task<byte[]?> SignAsync(byte[] challenge, bool create)
    {
        using var focus = BringPromptForward();
        var retrieval = create
            ? await KeyCredentialManager.RequestCreateAsync(CredentialName, KeyCredentialCreationOption.ReplaceExisting)
            : await KeyCredentialManager.OpenAsync(CredentialName);
        if (retrieval.Status != KeyCredentialStatus.Success)
        {
            return null;
        }
        var signed = await retrieval.Credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));
        if (signed.Status != KeyCredentialStatus.Success)
        {
            return null;
        }
        return signed.Result.ToArray();
    }

    /// <summary>
    /// Desktop apps' Hello prompts can open behind the app window. Watch for the prompt for a few
    /// seconds and bring it to the front.
    /// </summary>
    private static IDisposable BringPromptForward()
    {
        var stop = new CancellationTokenSource();
        var deadline = DateTime.UtcNow.AddSeconds(4);
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                nint prompt = Native.FindWindow("Credential Dialog Xaml Host", null);
                if (prompt != 0)
                {
                    Native.SetForegroundWindow(prompt);
                    return;
                }
                await Task.Delay(100);
            }
        });
        return new Stopper(stop);
    }

    private sealed class Stopper(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose() => cts.Cancel();
    }
}
