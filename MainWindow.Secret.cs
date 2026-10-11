// Secret gallery: pictures moved here are encrypted (AES-GCM) with a key made from a password (PBKDF2), in output/forge/secret.
// Nothing readable is left on disk, and the key is only in memory while the Secret tab is open.
// Forget the password and the pictures can't be opened: there is deliberately no way around it.
using System.IO;
using System.Security.Cryptography;
using System.Windows;

namespace ImageForge;

public partial class MainWindow
{
    static byte[] secretKey;            // while the Secret tab is open
    string SecretDir => Path.Combine(Output, "forge", "secret");
    string KeyFile => Path.Combine(SecretDir, "vault.key");       // salt (16) + SHA-256 of the key (32): checks a password, opens nothing

    static byte[] KeyFrom(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);

    // nonce (12) + tag (16) + the encrypted bytes
    static byte[] Seal(byte[] plain, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var data = new byte[plain.Length];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, data, tag);
        return [.. nonce, .. tag, .. data];
    }

    // throws with a wrong key or a changed file
    static byte[] Unseal(byte[] box, byte[] key)
    {
        var plain = new byte[box.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(box.AsSpan(0, 12), box.AsSpan(28), box.AsSpan(12, 16), plain);
        return plain;
    }

    // a secret picture is decrypted into memory, never written out
    static Stream OpenPicture(string path) => path.EndsWith(".locked") ? new MemoryStream(Unseal(File.ReadAllBytes(path), secretKey)) : File.OpenRead(path);

    // the password (a new one the first time) -> the key; null when cancelled
    byte[] AskKey()
    {
        string dir = SecretDir, keyFile = KeyFile;
        bool create = !File.Exists(keyFile);
        byte[] key = null;
        // the key takes a moment to make, on purpose (slows guessing): the dialog shows "Unlocking…" meanwhile
        new PasswordWindow(create, pw =>
        {
            if (create)
            {
                var salt = RandomNumberGenerator.GetBytes(16);
                var made = KeyFrom(pw, salt);
                Directory.CreateDirectory(dir);
                File.SetAttributes(dir, FileAttributes.Directory | FileAttributes.Hidden);
                File.WriteAllBytes(keyFile, [.. salt, .. SHA256.HashData(made)]);
                key = made;
                return null;
            }
            var saved = File.ReadAllBytes(keyFile);
            var k = KeyFrom(pw, saved[..16]);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(k), saved.AsSpan(16))) return "Wrong password. Try again.";
            key = k;
            return null;
        }) { Owner = this }.ShowDialog();
        return key;
    }

    // the Secret tab: password first, then its pictures; false when cancelled
    async Task<bool> OpenSecret()
    {
        var key = AskKey();
        if (key == null) return false;
        secretKey = key;
        var files = Directory.Exists(SecretDir) ? Directory.GetFiles(SecretDir, "*.locked") : [];
        var loaded = await Task.Run(() => files.Select(MakeShot).Where(s => s != null).ToList());
        if (secretKey != key) return true;                     // locked again while loading
        foreach (var s in loaded)
        {
            s.Favorite = settings.Favorites.Contains(Path.GetFileName(s.FilePath));
            s.Selecting = selectMode;
            shots.Add(s);
        }
        return true;
    }

    // leaving the Secret tab: forget the key and the decrypted pictures
    void LockSecret()
    {
        if (secretKey == null) return;
        CryptographicOperations.ZeroMemory(secretKey);
        secretKey = null;
        for (int i = shots.Count - 1; i >= 0; i--) if (shots[i].Secret) shots.RemoveAt(i);
    }

    // into the secret gallery, or back out to the gallery; how many moved
    int MoveSecret(List<Shot> picked)
    {
        if (picked.RemoveAll(s => s.IsVideo) > 0) Note("Videos can't go in the secret gallery yet.", true);
        picked.RemoveAll(s => s.Pending);
        if (picked.Count == 0) return 0;
        var key = secretKey ?? AskKey();
        if (key == null) return 0;
        int moved = 0;
        foreach (var s in picked)
            try { if (s.Secret) Unhide(s, key); else Hide(s, key); moved++; }
            catch (Exception ex) { Note("Couldn't move " + Path.GetFileName(s.FilePath) + ": " + ex.Message, true); }
        if (key != secretKey) CryptographicOperations.ZeroMemory(key);     // opened only to hide these
        return moved;
    }

    void Hide(Shot s, byte[] key)
    {
        Directory.CreateDirectory(Path.Combine(SecretDir, "originals"));
        var to = FreeName(SecretDir, Path.GetFileName(s.FilePath), ".locked");
        Recode(s.FilePath, to, b => Seal(b, key));
        if (File.Exists(OriginalOf(s.FilePath))) Recode(OriginalOf(s.FilePath), OriginalOf(to), b => Seal(b, key));   // an edit's before photo goes too
        if (settings.Favorites.Remove(Path.GetFileName(s.FilePath))) settings.Save();
        shots.Remove(s);
    }

    void Unhide(Shot s, byte[] key)
    {
        var to = FreeName(Path.Combine(Output, "forge"), Path.GetFileNameWithoutExtension(s.FilePath));     // "img_00012_.png.locked" -> "img_00012_.png"
        Recode(s.FilePath, to, b => Unseal(b, key));
        if (File.Exists(OriginalOf(s.FilePath)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OriginalOf(to)));
            Recode(OriginalOf(s.FilePath), OriginalOf(to), b => Unseal(b, key));
        }
        if (settings.Favorites.Remove(Path.GetFileName(s.FilePath))) settings.Save();
        shots.Remove(s);
        if (MakeShot(to) is { } back) { back.Selecting = selectMode; shots.Add(back); }     // the gallery sorts it in by date
    }

    // one file into another through f, keeping its date; the source is deleted outright (the Recycle Bin would keep a readable copy)
    static void Recode(string from, string to, Func<byte[], byte[]> f)
    {
        File.WriteAllBytes(to, f(File.ReadAllBytes(from)));
        File.SetLastWriteTime(to, File.GetLastWriteTime(from));
        File.Delete(from);
    }

    void SecretSelected_Click(object sender, RoutedEventArgs e)
    {
        bool hiding = ViewKind != "secret";
        int n = MoveSecret(Picked());
        if (n > 0) Note(hiding ? $"Moved {n} to the secret gallery." : $"Moved {n} out of the secret gallery.");
        UpdateSelection();
    }

    void MenuSecret_Click(object sender, RoutedEventArgs e)
    {
        if (ShotOf(sender) is { } s && MoveSecret([s]) > 0) Note(s.Secret ? "Moved out of the secret gallery." : "Moved to the secret gallery.");
    }

    void SecretViewed_Click(object sender, RoutedEventArgs e) =>
        RemoveViewed(s => MoveSecret([s]) > 0, viewing.Secret ? "Moved out of the secret gallery." : "Moved to the secret gallery.");

    // --selftest: a sealed picture loads with its key, and doesn't open with another
    static bool SelfTestSecret(string png)
    {
        var salt = new byte[16];
        var key = KeyFrom("right", salt);
        var box = Seal(File.ReadAllBytes(png), key);
        var locked = png + ".locked";
        try
        {
            File.WriteAllBytes(locked, box);
            secretKey = key;
            if (LoadImage(locked, 0).PixelWidth != 4) return false;
            try { Unseal(box, KeyFrom("wrong", salt)); return false; }
            catch (CryptographicException) { return true; }
        }
        finally { secretKey = null; File.Delete(locked); }
    }
}
