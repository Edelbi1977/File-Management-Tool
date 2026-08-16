// Id3v2TagLite.cs
//
// A minimal, dependency-free ID3v2 tag reader/writer. Replaces the need for
// TagLibSharp for the common case of reading/writing text frames (title,
// artist, album, genre, composer, comment, and date frames).
//
// Supports reading ID3v2.2, ID3v2.3, and ID3v2.4 tags. Always WRITES tags
// back out in ID3v2.3 format (the most broadly compatible with players).
//
// Frames this class doesn't specifically understand (album art / APIC,
// lyrics / USLT, custom TXXX frames, etc.) are preserved as raw bytes and
// written back unchanged, so saving won't destroy artwork or other data.
//
// ID3v1 fallback: many older MP3s (especially pre-2000s rips or ones tagged
// with old-OS-era software) only have a legacy ID3v1 tag (a 128-byte trailer
// at the end of the file), not ID3v2 at all. This class reads ID3v1 as a
// fallback for Title/Artist/Album/Comment/Year whenever no ID3v2 frame is
// present for that field, so mojibake fixing still works on those files.
// Saving writes a fresh ID3v2 tag AND, by default, strips the old ID3v1
// trailer — otherwise the stale, still-mojibake ID3v1 text would sit right
// next to the corrected ID3v2 text, which is confusing in players/taggers
// that surface both (pass stripLegacyId3v1: false to Save() to keep it).
//
// Known limitations (uncommon in practice, but worth knowing):
//   - Compressed or encrypted frames are not supported (rare in the wild).
//   - The extended header, if present, is skipped rather than parsed in detail.
//   - Unmapped ID3v2.2 frame types (very old tag format) are dropped on read.
//
// No NuGet packages required.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MyApp.Models;
    
internal class Id3RawFrame
{
    public string Id = string.Empty;
    public byte[] Flags = new byte[2];
    public byte[] Data = Array.Empty<byte>();
}

/// <summary>
/// Lightweight ID3v2 tag reader/writer with no external dependencies.
/// </summary>
public class Id3v2TagLite
{
    private readonly List<Id3RawFrame> _frames = new();
    private int _originalTagBlockLength; // bytes to skip at the start of the original file when saving

    public bool HasId3v2 { get; private set; }

    // ID3v1 fallback (128-byte trailer at end of file) — common in very old MP3s
    // that predate ID3v2 or were tagged with old-OS-era software. Read-only:
    // Save() always writes a fresh ID3v2 tag, which supersedes ID3v1 in every
    // modern player, so there's no need to also rewrite the legacy trailer.
    public bool HasId3v1 { get; private set; }
    private string? _v1Title, _v1Artist, _v1Album, _v1Year, _v1Comment;

    private static readonly Dictionary<string, string> V22ToV23Map = new()
    {
        { "TT2", "TIT2" }, { "TP1", "TPE1" }, { "TP2", "TPE2" },
        { "TAL", "TALB" }, { "TYE", "TYER" }, { "TCO", "TCON" },
        { "COM", "COMM" }, { "TOR", "TORY" }, { "TCM", "TCOM" },
    };

    // -----------------------------------------------------------
    // Reading
    // -----------------------------------------------------------

    public static Id3v2TagLite Read(string filePath)
    {
        var tag = new Id3v2TagLite();
        var allBytes = File.ReadAllBytes(filePath);

        ReadId3v1(allBytes, tag);

        if (allBytes.Length < 10 || allBytes[0] != 'I' || allBytes[1] != 'D' || allBytes[2] != '3')
        {
            tag.HasId3v2 = false;
            tag._originalTagBlockLength = 0;
            return tag;
        }

        byte majorVersion = allBytes[3];
        byte flags = allBytes[5];
        bool unsynchronized = (flags & 0x80) != 0;
        bool extendedHeader = (flags & 0x40) != 0;
        bool footerPresent = (flags & 0x10) != 0;

        int tagSize = ReadSyncSafeInt(allBytes, 6);
        int audioStart = 10 + tagSize + (footerPresent ? 10 : 0);
        tag._originalTagBlockLength = Math.Min(audioStart, allBytes.Length);
        tag.HasId3v2 = true;

        byte[] tagData = allBytes.Skip(10).Take(tagSize).ToArray();

        if (unsynchronized)
        {
            tagData = RemoveUnsynchronization(tagData);
        }

        int offset = 0;

        if (extendedHeader && tagData.Length >= 4)
        {
            if (majorVersion == 4)
            {
                int extSize = ReadSyncSafeInt(tagData, 0);
                offset += extSize;
            }
            else
            {
                int extSize = ReadBigEndianInt(tagData, 0, 4);
                offset += 4 + extSize;
            }
        }

        if (majorVersion == 2)
        {
            ReadV22Frames(tagData, offset, tag);
        }
        else
        {
            ReadV23OrV24Frames(tagData, offset, majorVersion, tag);
        }

        return tag;
    }

    private static void ReadId3v1(byte[] allBytes, Id3v2TagLite tag)
    {
        if (allBytes.Length < 128) return;

        int start = allBytes.Length - 128;
        if (allBytes[start] != 'T' || allBytes[start + 1] != 'A' || allBytes[start + 2] != 'G') return;

        tag.HasId3v1 = true;
        tag._v1Title = ReadId3v1Field(allBytes, start + 3, 30);
        tag._v1Artist = ReadId3v1Field(allBytes, start + 33, 30);
        tag._v1Album = ReadId3v1Field(allBytes, start + 63, 30);
        tag._v1Year = ReadId3v1Field(allBytes, start + 93, 4);
        tag._v1Comment = ReadId3v1Field(allBytes, start + 97, 30);
    }

    /// <summary>
    /// ID3v1 fields are raw single-byte text with no encoding marker — the same
    /// "1 code point = 1 byte" shape that ArabicMojibakeFixer.FixText expects,
    /// so mojibake fixing works on these exactly like it does on ID3v2 ISO-8859-1 text.
    /// </summary>
    private static string ReadId3v1Field(byte[] data, int offset, int length)
    {
        var raw = Encoding.GetEncoding("ISO-8859-1").GetString(data, offset, length);
        return raw.TrimEnd('\0', ' ');
    }

    private static void ReadV23OrV24Frames(byte[] tagData, int offset, byte majorVersion, Id3v2TagLite tag)
    {
        while (offset + 10 <= tagData.Length)
        {
            if (tagData[offset] == 0) break; // padding reached

            string frameId = Encoding.ASCII.GetString(tagData, offset, 4);
            if (!frameId.All(c => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) break;

            int frameSize = majorVersion == 4
                ? ReadSyncSafeInt(tagData, offset + 4)
                : ReadBigEndianInt(tagData, offset + 4, 4);

            if (frameSize < 0 || offset + 10 + frameSize > tagData.Length) break;

            var frame = new Id3RawFrame
            {
                Id = frameId,
                Flags = new[] { tagData[offset + 8], tagData[offset + 9] },
                Data = tagData.Skip(offset + 10).Take(frameSize).ToArray()
            };
            tag._frames.Add(frame);

            offset += 10 + frameSize;
        }
    }

    private static void ReadV22Frames(byte[] tagData, int offset, Id3v2TagLite tag)
    {
        while (offset + 6 <= tagData.Length)
        {
            if (tagData[offset] == 0) break; // padding reached

            string frameId3 = Encoding.ASCII.GetString(tagData, offset, 3);
            if (!frameId3.All(c => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) break;

            int frameSize = ReadBigEndianInt(tagData, offset + 3, 3);
            if (frameSize < 0 || offset + 6 + frameSize > tagData.Length) break;

            if (V22ToV23Map.TryGetValue(frameId3, out var mappedId))
            {
                var frame = new Id3RawFrame
                {
                    Id = mappedId,
                    Flags = new byte[2],
                    Data = tagData.Skip(offset + 6).Take(frameSize).ToArray()
                };
                tag._frames.Add(frame);
            }
            // Unmapped/exotic ID3v2.2 frames are dropped (see class-level limitations note).

            offset += 6 + frameSize;
        }
    }

    private static byte[] RemoveUnsynchronization(byte[] data)
    {
        var result = new List<byte>(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            result.Add(data[i]);
            if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0x00)
            {
                i++; // skip the inserted 0x00
            }
        }
        return result.ToArray();
    }

    // -----------------------------------------------------------
    // Text encoding/decoding
    // -----------------------------------------------------------

    private static string DecodeTextBody(byte encoding, byte[] bytes, int start, int length)
    {
        if (length <= 0) return string.Empty;

        switch (encoding)
        {
            case 0: // ISO-8859-1
                return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, length).TrimEnd('\0');
            case 3: // UTF-8 (v2.4 only)
                return Encoding.UTF8.GetString(bytes, start, length).TrimEnd('\0');
            case 2: // UTF-16BE, no BOM (v2.4 only)
                return Encoding.BigEndianUnicode.GetString(bytes, start, length).TrimEnd('\0');
            case 1: // UTF-16 with BOM
            default:
                if (length >= 2 && bytes[start] == 0xFF && bytes[start + 1] == 0xFE)
                    return Encoding.Unicode.GetString(bytes, start + 2, length - 2).TrimEnd('\0');
                if (length >= 2 && bytes[start] == 0xFE && bytes[start + 1] == 0xFF)
                    return Encoding.BigEndianUnicode.GetString(bytes, start + 2, length - 2).TrimEnd('\0');
                return Encoding.Unicode.GetString(bytes, start, length).TrimEnd('\0');
        }
    }

    private static string DecodeTextFrame(byte[] data)
    {
        if (data.Length == 0) return string.Empty;
        byte encoding = data[0];
        return DecodeTextBody(encoding, data, 1, data.Length - 1);
    }

    private static string DecodeCommentFrame(byte[] data)
    {
        if (data.Length < 4) return string.Empty;
        byte encoding = data[0];
        // data[1..4] = 3-byte language code, ignored
        int termLen = (encoding == 1 || encoding == 2) ? 2 : 1;
        int contentStart = 4;

        int termIndex = -1;
        for (int i = contentStart; i <= data.Length - termLen; i++)
        {
            bool isTerm = termLen == 1 ? data[i] == 0 : (data[i] == 0 && i + 1 < data.Length && data[i + 1] == 0);
            if (isTerm) { termIndex = i; break; }
        }
        if (termIndex < 0) return string.Empty;

        int commentStart = termIndex + termLen;
        if (commentStart >= data.Length) return string.Empty;

        return DecodeTextBody(encoding, data, commentStart, data.Length - commentStart);
    }

    /// <summary>Always encodes as UTF-16LE with BOM (encoding byte 1) — safely represents Arabic and any other script.</summary>
    private static byte[] EncodeTextFrame(string text)
    {
        var textBytes = Encoding.Unicode.GetBytes(text); // UTF-16LE, no BOM by itself
        var result = new byte[1 + 2 + textBytes.Length];
        result[0] = 1; // encoding: UTF-16 with BOM
        result[1] = 0xFF;
        result[2] = 0xFE;
        Array.Copy(textBytes, 0, result, 3, textBytes.Length);
        return result;
    }

    private static byte[] EncodeCommentFrame(string text)
    {
        var lang = new byte[] { (byte)'e', (byte)'n', (byte)'g' };
        var textBytes = Encoding.Unicode.GetBytes(text);
        // encoding(1) + language(3) + empty description terminator(2) + BOM(2) + text
        var result = new byte[1 + 3 + 2 + 2 + textBytes.Length];
        result[0] = 1;
        Array.Copy(lang, 0, result, 1, 3);
        result[4] = 0; result[5] = 0; // empty description, UTF-16 terminator
        result[6] = 0xFF; result[7] = 0xFE;
        Array.Copy(textBytes, 0, result, 8, textBytes.Length);
        return result;
    }

    // -----------------------------------------------------------
    // Frame get/set
    // -----------------------------------------------------------

    public string? GetText(string frameId)
    {
        var frame = _frames.FirstOrDefault(f => f.Id == frameId);
        if (frame != null)
        {
            return frameId == "COMM" ? DecodeCommentFrame(frame.Data) : DecodeTextFrame(frame.Data);
        }

        // Fall back to the legacy ID3v1 trailer if there's no ID3v2 frame for this field.
        if (HasId3v1)
        {
            switch (frameId)
            {
                case "TIT2": return string.IsNullOrEmpty(_v1Title) ? null : _v1Title;
                case "TPE1": return string.IsNullOrEmpty(_v1Artist) ? null : _v1Artist;
                case "TALB": return string.IsNullOrEmpty(_v1Album) ? null : _v1Album;
                case "COMM": return string.IsNullOrEmpty(_v1Comment) ? null : _v1Comment;
                case "TYER":
                case "TDRC": return string.IsNullOrEmpty(_v1Year) ? null : _v1Year;
            }
        }

        return null;
    }

    public void SetText(string frameId, string? value)
    {
        _frames.RemoveAll(f => f.Id == frameId);
        if (string.IsNullOrEmpty(value)) return;

        var data = frameId == "COMM" ? EncodeCommentFrame(value) : EncodeTextFrame(value);
        _frames.Add(new Id3RawFrame { Id = frameId, Flags = new byte[2], Data = data });
    }

    public string[] GetTextArray(string frameId)
    {
        var text = GetText(frameId);
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var parts = text.Contains('\0')
            ? text.Split('\0')
            : text.Split('/');
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
    }

    public void SetTextArray(string frameId, string[]? values)
    {
        if (values == null || values.Length == 0) { SetText(frameId, null); return; }
        SetText(frameId, string.Join("/", values));
    }

    // -----------------------------------------------------------
    // Convenience properties (mirrors the subset of TagLib.Tag used elsewhere)
    // -----------------------------------------------------------

    public string Title { get => GetText("TIT2") ?? string.Empty; set => SetText("TIT2", value); }
    public string Album { get => GetText("TALB") ?? string.Empty; set => SetText("TALB", value); }
    public string Comment { get => GetText("COMM") ?? string.Empty; set => SetText("COMM", value); }
    public string[] Performers { get => GetTextArray("TPE1"); set => SetTextArray("TPE1", value); }
    public string[] AlbumArtists { get => GetTextArray("TPE2"); set => SetTextArray("TPE2", value); }
    public string[] Genres { get => GetTextArray("TCON"); set => SetTextArray("TCON", value); }
    public string[] Composers { get => GetTextArray("TCOM"); set => SetTextArray("TCOM", value); }

    public string? RecordingDate => GetText("TDRC") ?? GetText("TYER");
    public string? OriginalReleaseDate => GetText("TDOR") ?? GetText("TORY");
    public string? ReleaseDate => GetText("TDRL");
    public string? EncodingDate => GetText("TDEN");

    public uint? Year
    {
        get
        {
            var date = RecordingDate;
            if (string.IsNullOrEmpty(date) || date.Length < 4) return null;
            return uint.TryParse(date.Substring(0, 4), out var y) ? y : (uint?)null;
        }
    }

    // -----------------------------------------------------------
    // Writing
    // -----------------------------------------------------------

    /// <summary>Builds the new ID3v2 tag header+frames and the (possibly ID3v1-stripped) audio bytes that follow it.</summary>
    private (byte[] TagBytes, byte[] AudioBytes) BuildNewFileContent(string sourceFilePath, bool stripLegacyId3v1)
    {
        using var framesBuffer = new MemoryStream();

        foreach (var frame in _frames)
        {
            var idBytes = Encoding.ASCII.GetBytes(frame.Id.PadRight(4).Substring(0, 4));
            var sizeBytes = WriteBigEndianInt(frame.Data.Length, 4);

            framesBuffer.Write(idBytes, 0, 4);
            framesBuffer.Write(sizeBytes, 0, 4);
            framesBuffer.Write(frame.Flags, 0, 2);
            framesBuffer.Write(frame.Data, 0, frame.Data.Length);
        }

        byte[] frameBytes = framesBuffer.ToArray();
        byte[] sizeSyncSafe = WriteSyncSafeInt(frameBytes.Length);

        using var newTag = new MemoryStream();
        newTag.Write(Encoding.ASCII.GetBytes("ID3"), 0, 3);
        newTag.WriteByte(3);   // major version: writing as ID3v2.3
        newTag.WriteByte(0);   // revision
        newTag.WriteByte(0);   // flags
        newTag.Write(sizeSyncSafe, 0, 4);
        newTag.Write(frameBytes, 0, frameBytes.Length);

        var originalBytes = File.ReadAllBytes(sourceFilePath);
        int audioStart = HasId3v2 ? Math.Min(_originalTagBlockLength, originalBytes.Length) : 0;
        var audioBytes = originalBytes.Skip(audioStart).ToArray();

        // Strip a trailing legacy ID3v1 tag so it can't sit alongside the new
        // ID3v2 tag showing stale, still-mojibake text (see class-level notes).
        if (stripLegacyId3v1 && audioBytes.Length >= 128)
        {
            int v1Start = audioBytes.Length - 128;
            if (audioBytes[v1Start] == 'T' && audioBytes[v1Start + 1] == 'A' && audioBytes[v1Start + 2] == 'G')
            {
                audioBytes = audioBytes.Take(v1Start).ToArray();
            }
        }

        return (newTag.ToArray(), audioBytes);
    }

    /// <summary>
    /// Writes the fixed tag directly to a NEW file (destinationFilePath), reading the
    /// original audio bytes from sourceFilePath. Use this when you're writing a fixed
    /// copy somewhere other than the source — e.g. into an output subdirectory — since
    /// it writes destinationFilePath in a single pass with no temp file and no delete
    /// step (there's nothing at that path yet to safely replace).
    /// sourceFilePath is left completely untouched.
    /// </summary>
    public void SaveAs(string sourceFilePath, string destinationFilePath, bool stripLegacyId3v1 = true)
    {
        var (tagBytes, audioBytes) = BuildNewFileContent(sourceFilePath, stripLegacyId3v1);

        using (var outStream = File.Create(destinationFilePath))
        {
            outStream.Write(tagBytes, 0, tagBytes.Length);
            outStream.Write(audioBytes, 0, audioBytes.Length);
        }

        DateTime DT = File.GetLastWriteTime(sourceFilePath);
        File.SetLastWriteTime(destinationFilePath, DT);


        if (stripLegacyId3v1) HasId3v1 = false;
        HasId3v2 = true;
        _originalTagBlockLength = tagBytes.Length;
    }

    /// <summary>
    /// Rewrites the tag IN PLACE on filePath (the same file this tag was read from).
    /// Writes to a temp file first and swaps it in (via File.Replace, atomic on
    /// Windows) so a crash mid-write can't leave filePath partially written.
    /// If you're writing to a different location than you read from — e.g. a fixed
    /// copy in an output folder — use <see cref="SaveAs"/> instead; it's both safer
    /// (no risk of touching the wrong file) and cheaper (no temp file needed).
    /// </summary>
    public void Save(string filePath, bool stripLegacyId3v1 = true)
    {
        var (tagBytes, audioBytes) = BuildNewFileContent(filePath, stripLegacyId3v1);

        var tempPath = filePath + ".tmp_tagwrite";
        using (var outStream = File.Create(tempPath))
        {
            outStream.Write(tagBytes, 0, tagBytes.Length);
            outStream.Write(audioBytes, 0, audioBytes.Length);
        }

        // File.Replace atomically swaps tempPath into filePath's place — safer than a
        // separate Delete + Move, which has a window where filePath doesn't exist.
        // (File.Replace is Windows-only; fall back to Delete+Move elsewhere.)
        if (File.Exists(filePath))
        {
            try
            {
                File.Replace(tempPath, filePath, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Delete(filePath);
                File.Move(tempPath, filePath);
            }
        }
        else
        {
            File.Move(tempPath, filePath);
        }

        if (stripLegacyId3v1) HasId3v1 = false;
        HasId3v2 = true;
        _originalTagBlockLength = tagBytes.Length;
    }

    // -----------------------------------------------------------
    // Byte helpers
    // -----------------------------------------------------------

    private static int ReadSyncSafeInt(byte[] data, int offset)
    {
        return ((data[offset] & 0x7F) << 21) |
               ((data[offset + 1] & 0x7F) << 14) |
               ((data[offset + 2] & 0x7F) << 7) |
               (data[offset + 3] & 0x7F);
    }

    private static byte[] WriteSyncSafeInt(int value)
    {
        return new[]
        {
            (byte)((value >> 21) & 0x7F),
            (byte)((value >> 14) & 0x7F),
            (byte)((value >> 7) & 0x7F),
            (byte)(value & 0x7F)
        };
    }

    private static int ReadBigEndianInt(byte[] data, int offset, int byteCount)
    {
        int result = 0;
        for (int i = 0; i < byteCount; i++)
        {
            result = (result << 8) | data[offset + i];
        }
        return result;
    }

    private static byte[] WriteBigEndianInt(int value, int byteCount)
    {
        var result = new byte[byteCount];
        for (int i = byteCount - 1; i >= 0; i--)
        {
            result[i] = (byte)(value & 0xFF);
            value >>= 8;
        }
        return result;
    }
}
