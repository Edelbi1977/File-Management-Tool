// MediaContentHasher.cs
//
// Computes a SHA1 hash over only the *content* portion of common media files — excluding
// metadata — so the hash stays stable when a file's tags/metadata change but the underlying
// audio/video/image data does not.
//
// No external libraries: only System.Security.Cryptography.SHA1 (part of the .NET BCL) is
// used for hashing; all container/frame parsing (JPEG markers, ID3 tags, ISO-BMFF boxes) is
// implemented from scratch.
//
// Content-region definitions (documented since they're a design choice, not a universal spec):
//   JPEG      : first non-APPn/COM marker (typically DQT/SOF) through EOI. Excludes the
//               metadata markers (EXIF/JFIF/ICC/Photoshop/XMP/COM) grouped at the start,
//               keeps everything that affects the decoded pixels (SOF/DQT/DHT/DRI/scan data).
//   MP3       : the MPEG frame data between an optional leading ID3v2 tag and an optional
//               trailing ID3v1 / APEv2 tag.
//   MP4 / MOV : the payload of the 'mdat' box(es) — actual encoded samples — excluding
//               'moov'/'udta'/'meta' (titles, GPS, chapters, etc).
//
// Usage:
//   var hasher = new MediaContentHasher("photo.jpg");
//   var range = hasher.LocateJpegContentRange();
//   byte[] hash = hasher.ComputeSha1(range.Start, range.End);
//   string hex = MediaContentHasher.ToHexString(hash);
//
//   // or, letting the class pick the locator from the file extension:
//   string hex2 = new MediaContentHasher("clip.mp4").ComputeContentHashHex();

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MyApp.Models

{
    public sealed class MediaContentHasher
    {
        public string FilePath { get; }

        public MediaContentHasher(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path is required.", nameof(filePath));
            if (!File.Exists(filePath)) throw new FileNotFoundException("Media file not found.", filePath);
            FilePath = filePath;
        }

        #region JPEG

        /// <summary>Scans JPEG markers to find where the leading metadata segments end and the
        /// "real" image structure (SOF/DQT/DHT/DRI/scan data) begins, through to EOI.</summary>
        public (long Start, long End) LocateJpegContentRange()
        {
            using var fs = File.OpenRead(FilePath);
            Span<byte> soi = stackalloc byte[2];
            if (fs.Read(soi) != 2 || soi[0] != 0xFF || soi[1] != 0xD8)
                throw new InvalidDataException("Not a valid JPEG file (missing SOI marker).");

            long contentStart = fs.Position;
            bool leadingMetadataPhase = true;
            byte[] lenBuf = new byte[2];

            byte? marker;
            while ((marker = ReadNextRealJpegMarker(fs)) != null)
            {
                byte m = marker.Value;

                if (m == 0xD9) // EOI
                    return (contentStart, fs.Position - 2);

                if (m == 0x01 || (m >= 0xD0 && m <= 0xD7)) // no-payload markers
                    continue;

                if (ReadExact(fs, lenBuf, 2) != 2)
                    break; // truncated file
                int len = (lenBuf[0] << 8) | lenBuf[1];
                long payloadStart = fs.Position;
                int payloadLen = Math.Max(0, len - 2);

                bool isAppOrComment = (m >= 0xE0 && m <= 0xEF) || m == 0xFE;
                if (leadingMetadataPhase && isAppOrComment)
                {
                    fs.Seek(payloadStart + payloadLen, SeekOrigin.Begin);
                    contentStart = fs.Position;
                    continue;
                }
                leadingMetadataPhase = false;

                // Skip this marker's payload (works uniformly for SOF/DQT/DHT/DRI/SOS headers —
                // any entropy-coded data that follows a SOS is naturally skipped over by the
                // byte-stuffing-aware scan on the next call to ReadNextRealJpegMarker).
                fs.Seek(payloadStart + payloadLen, SeekOrigin.Begin);
            }

            // No EOI found (truncated/malformed file) — fall back to end of file.
            return (contentStart, fs.Length);
        }

        /// <summary>Finds the next real JPEG marker byte from the current stream position,
        /// correctly skipping byte-stuffed 0xFF00 sequences and restart (RST) markers that occur
        /// inside entropy-coded scan data rather than mistaking them for a "real" marker.</summary>
        private static byte? ReadNextRealJpegMarker(Stream s)
        {
            while (true)
            {
                int b = s.ReadByte();
                if (b == -1) return null;
                if (b != 0xFF) continue;

                int c;
                do { c = s.ReadByte(); } while (c == 0xFF); // consume any run of padding 0xFF bytes
                if (c == -1) return null;
                if (c == 0x00) continue; // byte-stuffed literal 0xFF within entropy data — not a marker
                return (byte)c; // includes RST markers (0xD0-0xD7); caller treats those as no-payload
            }
        }

        #endregion

        #region MP3

        /// <summary>Finds the MPEG frame data by excluding a leading ID3v2 tag and a trailing
        /// ID3v1 (and, best-effort, APEv2) tag.</summary>
        public (long Start, long End) LocateMp3ContentRange()
        {
            using var fs = File.OpenRead(FilePath);
            long fileLength = fs.Length;

            long start = 0;
            byte[] id3v2Header = new byte[10];
            if (fileLength >= 10 && ReadExact(fs, id3v2Header, 10) == 10 &&
                id3v2Header[0] == 'I' && id3v2Header[1] == 'D' && id3v2Header[2] == '3')
            {
                bool footerPresent = (id3v2Header[5] & 0x10) != 0; // ID3v2.4 footer flag
                // Tag size is stored "syncsafe": 4 bytes, top bit of each byte always 0 (28 usable bits)
                int tagSize =
                    ((id3v2Header[6] & 0x7F) << 21) |
                    ((id3v2Header[7] & 0x7F) << 14) |
                    ((id3v2Header[8] & 0x7F) << 7) |
                    (id3v2Header[9] & 0x7F);
                start = 10 + tagSize + (footerPresent ? 10 : 0);
                if (start > fileLength) start = fileLength; // corrupt/truncated tag, don't go negative-length
            }

            long end = fileLength;

            if (fileLength - start >= 128)
            {
                fs.Seek(fileLength - 128, SeekOrigin.Begin);
                byte[] tag = new byte[3];
                if (ReadExact(fs, tag, 3) == 3 && tag[0] == 'T' && tag[1] == 'A' && tag[2] == 'G')
                    end = fileLength - 128;
            }

            end = TrimApeTagIfPresent(fs, end);

            if (start > end) start = end; // defensive: malformed/overlapping tags
            return (start, end);
        }

        /// <summary>Best-effort APEv2 tag exclusion (common right before ID3v1, or at EOF).
        /// APE tag layout details vary slightly by flags; this covers the common case.</summary>
        private static long TrimApeTagIfPresent(FileStream fs, long currentEnd)
        {
            const int footerSize = 32;
            if (currentEnd < footerSize) return currentEnd;

            fs.Seek(currentEnd - footerSize, SeekOrigin.Begin);
            byte[] footer = new byte[footerSize];
            if (ReadExact(fs, footer, footerSize) != footerSize) return currentEnd;

            if (Encoding.ASCII.GetString(footer, 0, 8) != "APETAGEX") return currentEnd;

            uint tagSize = BitConverter.ToUInt32(footer, 12); // little-endian; size of tag body + footer
            long tagStart = currentEnd - tagSize;
            return tagStart >= 0 ? tagStart : currentEnd;
        }

        /// <summary>Diagnostic info about what tag detection actually found — use this to verify
        /// whether a Start of 0 means "genuinely no ID3v2 tag" versus a detection problem.</summary>
        public sealed class Mp3TagInfo
        {
            public bool HasId3v2;
            public int Id3v2MajorVersion;
            public int Id3v2Size;         // tag body size only (not counting the 10-byte header)
            public bool Id3v2FooterPresent;
            public bool HasId3v1;
            public bool HasApeTag;
            public long ComputedContentStart;
            public long ComputedContentEnd;
            public string FirstBytesHex = "";  // first 16 bytes of the file, for manual sanity-checking

            public override string ToString() =>
                $"ID3v2: {(HasId3v2 ? $"yes (v2.{Id3v2MajorVersion}, {Id3v2Size} bytes{(Id3v2FooterPresent ? ", +footer" : "")})" : "no")}, " +
                $"ID3v1: {(HasId3v1 ? "yes" : "no")}, APEv2: {(HasApeTag ? "yes" : "no")}, " +
                $"content range: [{ComputedContentStart}, {ComputedContentEnd}), first bytes: {FirstBytesHex}";
        }

        /// <summary>Re-runs MP3 tag detection with full diagnostic output — call this on a file
        /// that reports Start == 0 to confirm whether that's really "no ID3v2 tag" (expected) or
        /// a detection problem (unexpected).</summary>
        public Mp3TagInfo InspectMp3Tags()
        {
            using var fs = File.OpenRead(FilePath);
            long fileLength = fs.Length;
            var info = new Mp3TagInfo();

            byte[] firstBytes = new byte[Math.Min(16, fileLength)];
            ReadExact(fs, firstBytes, firstBytes.Length);
            info.FirstBytesHex = BitConverter.ToString(firstBytes).Replace("-", " ");
            fs.Seek(0, SeekOrigin.Begin);

            long start = 0;
            byte[] id3v2Header = new byte[10];
            if (fileLength >= 10 && ReadExact(fs, id3v2Header, 10) == 10 &&
                id3v2Header[0] == 'I' && id3v2Header[1] == 'D' && id3v2Header[2] == '3')
            {
                info.HasId3v2 = true;
                info.Id3v2MajorVersion = id3v2Header[3];
                bool footerPresent = (id3v2Header[5] & 0x10) != 0;
                info.Id3v2FooterPresent = footerPresent;
                int tagSize =
                    ((id3v2Header[6] & 0x7F) << 21) |
                    ((id3v2Header[7] & 0x7F) << 14) |
                    ((id3v2Header[8] & 0x7F) << 7) |
                    (id3v2Header[9] & 0x7F);
                info.Id3v2Size = tagSize;
                start = 10 + tagSize + (footerPresent ? 10 : 0);
                if (start > fileLength) start = fileLength;
            }

            long end = fileLength;
            if (fileLength - start >= 128)
            {
                fs.Seek(fileLength - 128, SeekOrigin.Begin);
                byte[] tag = new byte[3];
                if (ReadExact(fs, tag, 3) == 3 && tag[0] == 'T' && tag[1] == 'A' && tag[2] == 'G')
                {
                    info.HasId3v1 = true;
                    end = fileLength - 128;
                }
            }

            long beforeApe = end;
            end = TrimApeTagIfPresent(fs, end);
            info.HasApeTag = end != beforeApe;

            if (start > end) start = end;
            info.ComputedContentStart = start;
            info.ComputedContentEnd = end;
            return info;
        }

        #endregion

        #region MP4 / MOV (shared ISO-BMFF / QuickTime box format)

        /// <summary>Locates the 'mdat' box's payload. If exactly one exists (the common case),
        /// returns it directly; for files with multiple 'mdat' boxes, returns a single range
        /// spanning the first to the last (which may include a few intervening non-content box
        /// headers) — use LocateAllMdatRanges() + ComputeSha1(ranges) for byte-exact hashing.</summary>
        public (long Start, long End) LocateMp4ContentRange() => CombineRanges(FindAllMdatBoxes());

        public (long Start, long End) LocateMovContentRange() => CombineRanges(FindAllMdatBoxes());

        /// <summary>All 'mdat' box payload ranges found at the top level of the file, in file order.
        /// Most files have exactly one; fragmented/edited files may have several.</summary>
        public List<(long Start, long End)> LocateAllMdatRanges() => FindAllMdatBoxes();

        private static (long Start, long End) CombineRanges(List<(long Start, long End)> ranges)
        {
            if (ranges.Count == 0)
                throw new InvalidDataException("No 'mdat' box found — not a valid/complete MP4 or MOV file.");
            return ranges.Count == 1 ? ranges[0] : (ranges[0].Start, ranges[^1].End);
        }

        private List<(long Start, long End)> FindAllMdatBoxes()
        {
            using var fs = File.OpenRead(FilePath);
            long fileLength = fs.Length;
            var ranges = new List<(long Start, long End)>();

            long pos = 0;
            byte[] header = new byte[8];
            byte[] largeSize = new byte[8];

            while (pos + 8 <= fileLength)
            {
                fs.Seek(pos, SeekOrigin.Begin);
                if (ReadExact(fs, header, 8) != 8) break;

                uint size32 = ReadU32BE(header, 0);
                string type = Encoding.ASCII.GetString(header, 4, 4);

                long boxHeaderSize = 8;
                long boxSize;
                if (size32 == 1)
                {
                    if (ReadExact(fs, largeSize, 8) != 8) break;
                    boxSize = ReadS64BE(largeSize, 0);
                    boxHeaderSize = 16;
                }
                else if (size32 == 0)
                {
                    boxSize = fileLength - pos; // box extends to end of file
                }
                else
                {
                    boxSize = size32;
                }

                if (boxSize < boxHeaderSize || pos + boxSize > fileLength)
                    break; // corrupt/truncated box table; stop scanning defensively

                if (type == "mdat")
                    ranges.Add((pos + boxHeaderSize, pos + boxSize));

                pos += boxSize;
            }

            return ranges;
        }

        private static uint ReadU32BE(byte[] b, int offset) =>
            ((uint)b[offset] << 24) | ((uint)b[offset + 1] << 16) | ((uint)b[offset + 2] << 8) | b[offset + 3];

        private static long ReadS64BE(byte[] b, int offset)
        {
            long v = 0;
            for (int i = 0; i < 8; i++) v = (v << 8) | b[offset + i];
            return v;
        }

        #endregion

        #region Hashing

        /// <summary>Computes SHA1 over the byte range [start, end) of the file, streaming in
        /// fixed-size chunks — never loading the whole range into memory at once.</summary>
        public byte[] ComputeSha1(long start, long end)
        {
            if (start < 0 || end < start) throw new ArgumentOutOfRangeException(nameof(end), "end must be >= start >= 0.");
            return ComputeSha1(new[] { (start, end) });
        }

        public byte[] ComputeSha1((long Start, long End) range) => ComputeSha1(range.Start, range.End);

        /// <summary>Computes a single SHA1 over multiple byte ranges concatenated in the given
        /// order — use this for formats where content isn't one contiguous span (e.g. multiple
        /// 'mdat' boxes in a fragmented MP4).</summary>
        public byte[] ComputeSha1(IEnumerable<(long Start, long End)> ranges)
        {
            using var fs = File.OpenRead(FilePath);
            using var sha1 = SHA1.Create();
            byte[] buffer = new byte[81920];

            foreach (var (start, end) in ranges)
            {
                if (start < 0 || end < start) throw new ArgumentOutOfRangeException(nameof(ranges), "Each range's end must be >= start >= 0.");
                fs.Seek(start, SeekOrigin.Begin);
                long remaining = end - start;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int got = fs.Read(buffer, 0, toRead);
                    if (got <= 0) throw new EndOfStreamException("File ended before the specified content range.");
                    sha1.TransformBlock(buffer, 0, got, null, 0);
                    remaining -= got;
                }
            }

            sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return sha1.Hash!;
        }

        /// <summary>Picks the right locator based on file extension and computes the content hash
        /// in one call. Throws NotSupportedException for unrecognized extensions.</summary>
        public byte[] ComputeContentHash()
        {
            string ext = Path.GetExtension(FilePath).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ComputeSha1(LocateJpegContentRange()),
                ".mp3" => ComputeSha1(LocateMp3ContentRange()),
                ".mp4" or ".m4a" or ".m4v" or ".mov" or ".qt" => ComputeSha1(LocateAllMdatRanges()),
                _ => throw new NotSupportedException($"No content-range locator for extension '{ext}'.")
            };
        }

        public string ComputeContentHashHex() => ToHexString(ComputeContentHash());

        public static string ToHexString(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

        #endregion

        #region Small IO helper

        private static int ReadExact(Stream s, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buffer, total, count - total);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        #endregion
    }
}
