using Vesper.Models;

namespace Vesper.Services;

public static class SavedPasswords
{
    /// <summary>
    /// Applies the Mac editor's password choices. A saved password follows the Mac's edited
    /// address, account and security settings, but only from the settings it was saved for, so
    /// an edit can't adopt a password that a tampered profile had already lost.
    /// </summary>
    public static async Task ApplyEditAsync(SecretVault vault, MacProfile? before, MacProfile after, string? newPassword, bool remember, Func<Task<bool>> unlock)
    {
        if (newPassword is { Length: > 0 } && remember)
        {
            if (await unlock())
            {
                vault.SetPassword(after, newPassword);
                after.HasSavedPassword = true;
            }
        }
        else if (!remember)
        {
            if (after.HasSavedPassword && await unlock()) vault.Forget(after.Id);
            after.HasSavedPassword = false;
        }
        else if (after.HasSavedPassword && before is not null)
        {
            byte[] from = SecretVault.Binding(before), to = SecretVault.Binding(after);
            if (!from.AsSpan().SequenceEqual(to) && await unlock()) vault.Rebind(after.Id, from, to);
        }
    }
}
