using System;
using System.Text;

namespace RestVtp.Plugins
{
    /// <summary>
    /// Dataverse requires every virtual table row to have a GUID primary key.
    /// External REST APIs usually key on ints or short strings. Because
    /// plug-ins are stateless we cannot keep a hash->key lookup, so the key
    /// must be REVERSIBLY embedded in the GUID itself:
    ///
    ///   guid  : source key is already a GUID, pass through.
    ///   int   : value stored big-endian in the last 8 bytes, marker 0x01.
    ///   string: UTF-8 bytes (max 14) stored in bytes 2..15, length in
    ///           byte 1, marker 0x02.
    ///
    /// Strings longer than 14 UTF-8 bytes are not supported in v1. The
    /// SwaggerForge tool validates this at design time.
    /// </summary>
    public static class GuidSynthesizer
    {
        private const byte MarkerInt = 0x01;
        private const byte MarkerString = 0x02;

        public static Guid ToGuid(string keyKind, string rawKey)
        {
            if (string.IsNullOrEmpty(rawKey))
                throw new InvalidOperationException("RestVtp: source record has an empty key.");

            switch ((keyKind ?? "string").ToLowerInvariant())
            {
                case "guid":
                    return Guid.Parse(rawKey);

                case "int":
                    var b = new byte[16];
                    b[0] = MarkerInt;
                    var val = long.Parse(rawKey);
                    for (int i = 0; i < 8; i++)
                        b[15 - i] = (byte)(val >> (i * 8));
                    return new Guid(b);

                case "string":
                    var utf8 = Encoding.UTF8.GetBytes(rawKey);
                    if (utf8.Length > 14)
                        throw new InvalidOperationException(
                            $"RestVtp: string key '{rawKey}' exceeds 14 UTF-8 bytes and cannot be embedded in a GUID. " +
                            "Use keyKind 'guid'/'int', or shorten the source key.");
                    var sb = new byte[16];
                    sb[0] = MarkerString;
                    sb[1] = (byte)utf8.Length;
                    Array.Copy(utf8, 0, sb, 2, utf8.Length);
                    return new Guid(sb);

                default:
                    throw new InvalidOperationException($"RestVtp: unknown keyKind '{keyKind}'.");
            }
        }

        public static string FromGuid(string keyKind, Guid id)
        {
            switch ((keyKind ?? "string").ToLowerInvariant())
            {
                case "guid":
                    return id.ToString();

                case "int":
                    var b = id.ToByteArray();
                    if (b[0] != MarkerInt)
                        throw new InvalidOperationException("RestVtp: GUID was not synthesised from an int key.");
                    long val = 0;
                    for (int i = 0; i < 8; i++)
                        val = (val << 8) | b[8 + i];
                    return val.ToString();

                case "string":
                    var sb = id.ToByteArray();
                    if (sb[0] != MarkerString)
                        throw new InvalidOperationException("RestVtp: GUID was not synthesised from a string key.");
                    int len = sb[1];
                    return Encoding.UTF8.GetString(sb, 2, len);

                default:
                    throw new InvalidOperationException($"RestVtp: unknown keyKind '{keyKind}'.");
            }
        }
    }
}
