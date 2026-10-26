using System.Security.Cryptography;
using Vesper.Security;
using Vesper.Updates;

// Vesper's release signing.
//   keygen                      make the release key (once), print its public half
//   public-key                  print the public half
//   manifest <dist> <version> <summary>   describe the release's downloads in <dist>
//   sign <manifest>             sign it (writes <manifest>.sig) and check it against the app's key
//   verify <manifest>           check a signature with the key built into Vesper
//   backup <file>               write a passphrase-encrypted copy of the key (asks for the passphrase)
//   restore <file>              put a backed-up key on this PC
//   check <as-version> <kind>   update the way an installed copy would, from the live releases
//                               page, pretending to be <as-version> (downloads into %TEMP% only)

string keyFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vesper-release");
string keyFile = Path.Combine(keyFolder, "update-signing.key");
var sealer = new DpapiProtector("Vesper release signing key v1"u8);

try
{
    switch (args)
    {
        case ["keygen"]:
            if (File.Exists(keyFile)) throw new InvalidOperationException($"A release key already exists ({keyFile}). Losing or replacing it stops installed copies updating.");
            Directory.CreateDirectory(keyFolder);
            using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                byte[] pkcs8 = key.ExportPkcs8PrivateKey();
                File.WriteAllBytes(keyFile, sealer.Protect(pkcs8));
                CryptographicOperations.ZeroMemory(pkcs8);
                Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
            }
            return 0;

        case ["public-key"]:
            using (var key = LoadKey())
            {
                Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
            }
            return 0;

        case ["manifest", var dist, var version, var summary]:
        {
            var manifest = new UpdateManifest
            {
                Product = UpdateManifest.ProductName,
                Version = version,
                Published = DateTimeOffset.UtcNow,
                Summary = summary,
            };
            var files = new Dictionary<string, string>
            {
                ["windows-x64-setup"] = $"Vesper-{version}-Setup-x64.exe",
                ["windows-arm64-setup"] = $"Vesper-{version}-Setup-arm64.exe",
                ["windows-x64-portable"] = $"Vesper-{version}-win-x64.zip",
                ["windows-arm64-portable"] = $"Vesper-{version}-win-arm64.zip",
                ["linux-x64-pacman"] = $"vesper-{version}-1-x86_64.pkg.tar.zst",
                ["linux-x64-appimage"] = $"Vesper-{version}-x86_64.AppImage",
            };
            foreach (var (kind, name) in files)
            {
                string path = Path.Combine(dist, name);
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"  (no {name}: {kind} copies won't be offered this update)");
                    continue;
                }
                using var stream = File.OpenRead(path);
                manifest.Assets[kind] = new UpdateAsset
                {
                    File = name,
                    Size = stream.Length,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)),
                };
            }
            byte[] json = manifest.ToJson();
            UpdateManifest.Parse(json); // the app's own checks
            string output = Path.Combine(dist, "vesper-update.json");
            File.WriteAllBytes(output, json);
            Console.WriteLine($"Wrote {output} ({manifest.Assets.Count} downloads)");
            return 0;
        }

        case ["sign", var manifestPath]:
        {
            byte[] json = File.ReadAllBytes(manifestPath);
            UpdateManifest.Parse(json);
            string signature;
            using (var key = LoadKey())
            {
                signature = UpdateSigning.Sign(json, key);
            }
            if (!UpdateSigning.Verify(json, signature))
            {
                throw new InvalidOperationException("Signed, but the key built into Vesper (UpdateSigning.PublicKey) doesn't match the release key, so no installed copy would accept it.");
            }
            File.WriteAllText(manifestPath + ".sig", signature);
            Console.WriteLine($"Signed {manifestPath}");
            return 0;
        }

        case ["verify", var manifestPath]:
        {
            byte[] json = File.ReadAllBytes(manifestPath);
            bool ok = UpdateSigning.Verify(json, File.ReadAllText(manifestPath + ".sig"));
            Console.WriteLine(ok ? "Signature OK" : "Signature INVALID");
            return ok ? 0 : 2;
        }

        case ["backup", var output]:
        {
            string passphrase = ReadSecret("Passphrase for the backup (12+ characters): ");
            if (passphrase.Length < 12 || ReadSecret("Again: ") != passphrase) throw new InvalidOperationException("Passphrases too short or different.");
            using var key = LoadKey();
            var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);
            File.WriteAllText(output, key.ExportEncryptedPkcs8PrivateKeyPem(passphrase, pbe));
            Console.WriteLine($"Wrote {output}. Keep it and the passphrase somewhere safe and apart.");
            return 0;
        }

        case ["restore", var input]:
        {
            if (File.Exists(keyFile)) throw new InvalidOperationException($"A release key already exists ({keyFile}).");
            using var key = ECDsa.Create();
            key.ImportFromEncryptedPem(File.ReadAllText(input), ReadSecret("Passphrase: "));
            Directory.CreateDirectory(keyFolder);
            byte[] pkcs8 = key.ExportPkcs8PrivateKey();
            File.WriteAllBytes(keyFile, sealer.Protect(pkcs8));
            CryptographicOperations.ZeroMemory(pkcs8);
            Console.WriteLine("Restored. Public key: " + Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
            return 0;
        }

        case ["check", var asVersion, var kind]:
        {
            string folder = Path.Combine(Path.GetTempPath(), "vesper-update-check-" + Guid.NewGuid().ToString("N"));
            var data = new Vesper.Services.AppData(folder);
            using var service = new UpdateService(data, Version.Parse(asVersion), null, UpdateSigning.PublicKey, kind);
            await service.CheckAsync(manual: true);
            Console.WriteLine($"Status: {service.Status}; offered {service.Offer?.Version ?? "nothing"}");
            if (service.Error is { } error) Console.WriteLine("Error: " + error);
            if (service.DownloadedFile is { } file) Console.WriteLine($"Downloaded and verified: {file} ({new FileInfo(file).Length:N0} bytes)");
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
            return service.Status is UpdateStatus.Ready or UpdateStatus.UpToDate ? 0 : 2;
        }

        default:
            Console.Error.WriteLine("usage: keygen | public-key | manifest <dist> <version> <summary> | sign <manifest> | verify <manifest> | backup <file> | restore <file> | check <as-version> <kind>");
            return 1;
    }
}
catch (Exception e) when (e is InvalidOperationException or IOException or CryptographicException or InvalidDataException)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 2;
}

ECDsa LoadKey()
{
    if (!File.Exists(keyFile)) throw new InvalidOperationException($"No release key at {keyFile}. Run keygen first.");
    byte[] pkcs8 = sealer.Unprotect(File.ReadAllBytes(keyFile));
    try
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return key;
    }
    finally
    {
        CryptographicOperations.ZeroMemory(pkcs8);
    }
}

static string ReadSecret(string prompt)
{
    Console.Write(prompt);
    var text = new System.Text.StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; continue; }
        text.Append(k.KeyChar);
    }
    Console.WriteLine();
    return text.ToString();
}
