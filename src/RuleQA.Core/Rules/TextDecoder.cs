using System.Text;

namespace RuleQA.Core.Rules;

/// <summary>Reads rule files saved as UTF-8 (with or without BOM), UTF-16 with BOM, or Shift_JIS (CP932).</summary>
public static class TextDecoder
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static TextDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <returns>The text and the name of the encoding that was used.</returns>
    public static (string Text, string Encoding) Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8 (BOM)");
        if (bytes is [0xFF, 0xFE, ..]) return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16");
        if (bytes is [0xFE, 0xFF, ..]) return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16BE");
        try
        {
            return (StrictUtf8.GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding(932).GetString(bytes), "Shift_JIS");
        }
    }
}
