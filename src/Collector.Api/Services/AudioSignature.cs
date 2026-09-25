namespace Collector.Api.Services;

/// <summary>
/// Checks that an upload really is the audio container its extension claims, from its
/// first bytes ("magic numbers") rather than the client-declared content type.
/// </summary>
public static class AudioSignature
{
    /// <summary>Bytes needed to recognise every supported format.</summary>
    public const int HeaderLength = 12;

    private static ReadOnlySpan<byte> WebMMagic => [0x1A, 0x45, 0xDF, 0xA3];

    public static bool Matches(string extension, ReadOnlySpan<byte> header) =>
        extension.ToLowerInvariant() switch
        {
            // ID3v2 tag, or a bare MPEG audio frame (11 sync bits set).
            ".mp3" => header.StartsWith("ID3"u8)
                || (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0),
            ".ogg" => header.StartsWith("OggS"u8),
            // ISO base media file: box size (4 bytes) then "ftyp".
            ".m4a" => header.Length >= 8 && header[4..8].SequenceEqual("ftyp"u8),
            ".webm" => header.StartsWith(WebMMagic),
            _ => false,
        };
}
