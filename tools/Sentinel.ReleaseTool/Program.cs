using Sentinel.Core.Updates;

// Release signing for Sentinel's auto-updater.
//   keygen <private-key.pem>          create a P-256 key pair; prints the public key to embed in UpdateSignature.cs
//   sign <package.zip> <private.pem>   writes <package.zip>.sig
//   verify <package.zip> [public.pem]  verifies against the given key, or the key built into this build
//   pack <folder> <package.zip>        zips a published folder (forward-slash entry names, no base folder)

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: keygen <private.pem> | sign <file> <private.pem> | verify <file> [public.pem] | pack <folder> <zip>");
    return 1;
}

switch (args[0])
{
    case "keygen" when args.Length == 2:
    {
        if (File.Exists(args[1]))
        {
            Console.Error.WriteLine($"{args[1]} already exists; refusing to overwrite a signing key.");
            return 2;
        }
        var (priv, pub) = UpdateSignature.CreateKeyPair();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
        File.WriteAllText(args[1], priv);
        Console.WriteLine(pub);
        Console.Error.WriteLine("Fingerprint: " + UpdateSignature.Fingerprint(pub));
        return 0;
    }
    case "sign" when args.Length == 3:
    {
        string sig;
        using (var f = File.OpenRead(args[1])) sig = UpdateSignature.Sign(f, File.ReadAllText(args[2]));
        File.WriteAllText(args[1] + UpdateFeed.SignatureSuffix, sig);
        Console.WriteLine($"Signed {Path.GetFileName(args[1])}");
        return 0;
    }
    case "verify" when args.Length is 2 or 3:
    {
        var key = args.Length == 3 ? File.ReadAllText(args[2]) : UpdateSignature.OfficialPublicKeyPem;
        using var f = File.OpenRead(args[1]);
        var ok = UpdateSignature.Verify(f, File.ReadAllText(args[1] + UpdateFeed.SignatureSuffix), key);
        Console.WriteLine(ok ? "Signature OK" : "Signature INVALID");
        return ok ? 0 : 3;
    }
    case "pack" when args.Length == 3:
    {
        if (File.Exists(args[2])) File.Delete(args[2]);
        System.IO.Compression.ZipFile.CreateFromDirectory(args[1], args[2], System.IO.Compression.CompressionLevel.SmallestSize, includeBaseDirectory: false);
        Console.WriteLine($"Packed {Path.GetFileName(args[2])} ({new FileInfo(args[2]).Length / 1024 / 1024} MB)");
        return 0;
    }
    default:
        Console.Error.WriteLine("Unknown command.");
        return 1;
}
