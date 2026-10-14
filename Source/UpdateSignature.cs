using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace PoGGaduchy;

/// <summary>
/// Weryfikacja podpisu wydania (ECDSA P-256, SHA-256). Klucz publiczny jest wbudowany w program (Assets\update-public-key.txt),
/// klucz prywatny ma tylko autor wydań (tools\podpisz-wydanie.ps1). Podpisywany jest tekst
/// "PoGGaduchy-release\n&lt;wersja&gt;\n&lt;SHA-256 pliku zip&gt;", więc podpis nie pasuje do innego pliku ani do innego numeru wersji.
/// Bez poprawnego klucza lub podpisu aktualizacja nie jest instalowana.
/// </summary>
public static class UpdateSignature
{
    /// <summary>Klucz publiczny (SPKI w base64) z zasobów programu albo null, gdy go nie ma.</summary>
    public static string? EmbeddedKey()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("update-public-key.txt");
        if (s == null) return null;
        using var r = new StreamReader(s, Encoding.UTF8);
        var t = r.ReadToEnd().Trim();
        return t.Length == 0 ? null : t;
    }

    public static bool Verify(string? publicKeyBase64, string version, string sha256Hex, string signatureBase64)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(publicKeyBase64)) return false;
            var sig = Convert.FromBase64String(signatureBase64.Trim());
            if (sig.Length != 64) return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64.Trim()), out _);
            var data = Encoding.UTF8.GetBytes($"PoGGaduchy-release\n{version}\n{sha256Hex.ToLowerInvariant()}");
            return key.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch { return false; }
    }
}
