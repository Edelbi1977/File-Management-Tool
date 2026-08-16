// JpegMetadata.cs
//
// Native JPEG metadata reader/writer — STREAMING version.
//
// Load() scans the file marker-by-marker using Seek/Read on the underlying Stream.
// Only the payload of markers we actually parse (APP0/JFIF, APP1/Exif, APP1/XMP,
// APP13/Photoshop-IPTC, COM) is read into memory — each is capped at ~64KB by the
// JPEG spec itself, so this is always small. Every other marker (DQT, SOF, DHT, DRI,
// unrelated APPn, and above all the entropy-coded image data after SOS, which is
// normally the vast majority of the file) is left on disk: only its (offset, length)
// is recorded, and it is streamed straight from source to destination in small
// buffered chunks on Save() — it is never loaded into memory as a whole.
//
// Because Save() needs to stream those untouched regions back out of the original
// file, the source stream is kept open for the lifetime of this object. Dispose()
// (or a using-block) closes it if this object opened it itself (Load(string path)).
//
// Writing is supported for EXIF tags (IFD0 / SubIFD / GPS) and the JPEG comment (COM).
// JFIF, IPTC and XMP are read-only in this version but are still replayed byte-for-byte
// on Save() from their small cached payload.
//
// Usage:
//   using var jm = new JpegMetadataFile();
//   jm.Load("photo.jpg");                       // only touches marker headers + small metadata payloads
//
//   foreach (var entry in jm.GetAllMetadata())
//       Console.WriteLine(entry);
//
//   jm.SetExifAscii(ExifTags.Artist, "Bashar");
//   jm.SetComment("Edited via JpegMetadataFile");
//
//   jm.Save("photo_edited.jpg");                // untouched segments + image data are streamed, not buffered

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MyApp.Models

{
    #region Public enums / value types

    /// <summary>TIFF/EXIF field data types (values match the EXIF spec's "Type" field).</summary>
    public enum ExifDataType : ushort
    {
        Byte = 1,
        Ascii = 2,
        Short = 3,
        Long = 4,
        Rational = 5,
        SByte = 6,
        Undefined = 7,
        SShort = 8,
        SLong = 9,
        SRational = 10,
        Float = 11,
        Double = 12
    }

    /// <summary>Which logical metadata block an entry belongs to.</summary>
    public enum MetadataGroup
    {
        Jfif,
        ExifIfd0,
        ExifSubIfd,
        ExifGps,
        Iptc,
        Xmp,
        Comment,
        Other
    }

    /// <summary>An unsigned TIFF rational (numerator/denominator).</summary>
    public readonly struct URational
    {
        public readonly uint Numerator;
        public readonly uint Denominator;
        public URational(uint n, uint d) { Numerator = n; Denominator = d; }
        public double ToDouble() => Denominator == 0 ? 0 : (double)Numerator / Denominator;
        public override string ToString() => $"{Numerator}/{Denominator}";
    }

    /// <summary>A signed TIFF rational (numerator/denominator).</summary>
    public readonly struct SRational
    {
        public readonly int Numerator;
        public readonly int Denominator;
        public SRational(int n, int d) { Numerator = n; Denominator = d; }
        public double ToDouble() => Denominator == 0 ? 0 : (double)Numerator / Denominator;
        public override string ToString() => $"{Numerator}/{Denominator}";
    }

    /// <summary>One piece of metadata discovered in the file, ready for display.</summary>
    public sealed class MetadataEntry
    {
        public MetadataGroup Group { get; init; }
        public int TagId { get; init; }
        public string Name { get; init; } = "";
        public ExifDataType DataType { get; init; }
        public int Count { get; init; }
        public object? Value { get; init; }
        public Type ClrType => Value?.GetType() ?? typeof(object);

        public override string ToString()
        {
            string val = FormatValue();
            return Group is MetadataGroup.Jfif or MetadataGroup.Comment or MetadataGroup.Iptc or MetadataGroup.Xmp
                ? $"[{Group}] {Name} : {val}"
                : $"[{Group}] {Name} (0x{TagId:X4}) : {DataType} x{Count} = {val}";
        }

        private string FormatValue()
        {
            if (Value is null) return "(null)";
            if (Value is byte[] bytes)
                return bytes.Length <= 32
                    ? BitConverter.ToString(bytes).Replace("-", " ")
                    : $"{bytes.Length} bytes";
            if (Value is System.Collections.IEnumerable en and not string)
                return "[" + string.Join(", ", en.Cast<object>()) + "]";
            return Value.ToString() ?? "";
        }
    }

    #endregion

    /// <summary>One raw TIFF/EXIF IFD entry (tag/type/count/value), value bytes always stored big-endian.</summary>
    public sealed class IfdEntry
    {
        public ushort Tag;
        public ExifDataType Type;
        public uint Count;
        /// <summary>Raw value bytes, always in big-endian order regardless of source file endianness.</summary>
        public byte[] ValueBytes = Array.Empty<byte>();
    }

    /// <summary>Well-known EXIF tag IDs for convenience.</summary>
    public static class ExifTags
    {
        // IFD0
        public const int ImageDescription = 0x010E;
        public const int Make = 0x010F;
        public const int Model = 0x0110;
        public const int Orientation = 0x0112;
        public const int XResolution = 0x011A;
        public const int YResolution = 0x011B;
        public const int ResolutionUnit = 0x0128;
        public const int Software = 0x0131;
        public const int DateTime = 0x0132;
        public const int Artist = 0x013B;
        public const int Copyright = 0x8298;
        internal const int ExifIfdPointer = 0x8769;
        internal const int GpsIfdPointer = 0x8825;

        // EXIF SubIFD
        public const int ExposureTime = 0x829A;
        public const int FNumber = 0x829D;
        public const int ExposureProgram = 0x8822;
        public const int IsoSpeedRatings = 0x8827;
        public const int ExifVersion = 0x9000;
        public const int DateTimeOriginal = 0x9003;
        public const int DateTimeDigitized = 0x9004;
        public const int FocalLength = 0x920A;
        public const int Flash = 0x9209;
        public const int PixelXDimension = 0xA002;
        public const int PixelYDimension = 0xA003;
        public const int ExposureMode = 0xA402;
        public const int WhiteBalance = 0xA403;
        public const int FocalLengthIn35mmFilm = 0xA405;
        public const int SceneCaptureType = 0xA406;
        public const int LensModel = 0xA434;

        // GPS IFD
        public const int GPSLatitudeRef = 0x0001;
        public const int GPSLatitude = 0x0002;
        public const int GPSLongitudeRef = 0x0003;
        public const int GPSLongitude = 0x0004;
        public const int GPSAltitudeRef = 0x0005;
        public const int GPSAltitude = 0x0006;
        public const int GPSTimeStamp = 0x0007;
        public const int GPSDateStamp = 0x001D;
    }

    /// <summary>Which IFD to target for a read/write/remove operation.</summary>
    public enum IfdTarget { Ifd0, ExifSubIfd, Gps }

    public sealed class JpegMetadataFile : IDisposable
    {
        /// <summary>
        /// One JPEG segment as tracked internally. Either fully cached in memory (small
        /// metadata segments we parse or that were newly created/modified) or a passthrough
        /// reference (offset+length) into the still-open source stream for everything else.
        /// </summary>
        private sealed class SegmentRef
        {
            public byte Marker;
            public byte[]? Payload;     // non-null => cached in memory (small)
            public long SourceOffset;   // valid only when Payload == null
            public int SourceLength;    // valid only when Payload == null

            /// <summary>Total on-disk size (header + payload) this segment occupied in the
            /// original file, or -1 if this segment never existed on disk (created this session).
            /// Needed for SaveInPlaceBuffered() to keep its source-consumption bookkeeping in
            /// sync even for segments we cache in memory, modify, or logically delete.</summary>
            public int OriginalTotalLength = -1;

            /// <summary>True if this segment (which did exist in the original file, so it still
            /// needs its original bytes accounted for) has been logically removed and should be
            /// skipped rather than written out.</summary>
            public bool Deleted;
        }

        private Stream? _source;
        private bool _ownsSource;
        private string? _sourcePath;
        private long _entropyDataOffset; // offset in _source where post-SOS raw bytes (image data + EOI) begin
        private readonly List<SegmentRef> _segments = new();

        private bool _hasExif;
        private int _exifSegmentIndex = -1;
        private int _commentSegmentIndex = -1;

        public List<IfdEntry> Ifd0Entries { get; private set; } = new();
        public List<IfdEntry> ExifSubIfdEntries { get; private set; } = new();
        public List<IfdEntry> GpsIfdEntries { get; private set; } = new();

        private byte[]? _jfifPayload;
        private byte[]? _photoshopPayload;
        private string? _xmpXml;
        private int? _imageWidth;
        private int? _imageHeight;

        private static readonly byte[] ExifId = Encoding.ASCII.GetBytes("Exif\0\0");
        private static readonly byte[] JfifId = Encoding.ASCII.GetBytes("JFIF\0");
        private static readonly byte[] XmpId = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        private static readonly byte[] PhotoshopId = Encoding.ASCII.GetBytes("Photoshop 3.0\0");

        #region Load (streaming scan — only required markers are read)

        /// <summary>Opens the file and keeps it open (needed by Save()); disposed by Dispose().</summary>
        public void Load(string path)
        {
            Close();
            _source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _ownsSource = true;
            _sourcePath = path;
            Scan();
        }

        /// <summary>Scans an already-open seekable stream. Caller keeps ownership and must keep it
        /// open until after Save() is called.</summary>
        public void Load(Stream source)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (!source.CanSeek || !source.CanRead)
                throw new ArgumentException("Stream must be readable and seekable for marker-by-marker scanning.");
            Close();
            _source = source;
            _ownsSource = false;
            _sourcePath = null;
            Scan();
        }

        private void Scan()
        {
            var s = _source!;
            _segments.Clear();
            _hasExif = false;
            _exifSegmentIndex = -1;
            _commentSegmentIndex = -1;
            Ifd0Entries = new List<IfdEntry>();
            ExifSubIfdEntries = new List<IfdEntry>();
            GpsIfdEntries = new List<IfdEntry>();
            _jfifPayload = null;
            _photoshopPayload = null;
            _xmpXml = null;
            _imageWidth = null;
            _imageHeight = null;

            s.Seek(0, SeekOrigin.Begin);
            byte[] two = new byte[2];
            ReadExact(s, two, 2);
            if (two[0] != 0xFF || two[1] != 0xD8)
                throw new InvalidDataException("Not a valid JPEG file (missing SOI marker).");

            while (true)
            {
                int b = s.ReadByte();
                if (b == -1) throw new InvalidDataException("Unexpected end of stream while scanning markers.");
                if (b != 0xFF) continue; // resync on stray bytes

                long ffPos = s.Position - 1;
                while (b == 0xFF) b = s.ReadByte();
                if (b == -1) throw new InvalidDataException("Truncated JPEG stream.");
                byte marker = (byte)b;

                if (marker == 0xD9) // EOI with no scan data at all (degenerate/malformed but handle gracefully)
                {
                    _entropyDataOffset = ffPos;
                    return;
                }

                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    _segments.Add(new SegmentRef { Marker = marker, Payload = Array.Empty<byte>(), OriginalTotalLength = 2 });
                    continue;
                }

                ReadExact(s, two, 2);
                int len = (two[0] << 8) | two[1];
                long payloadStart = s.Position;
                int payloadLen = len - 2;

                if (marker == 0xDA) // Start Of Scan: small header we cache, then raw entropy data to EOF (never read here)
                {
                    byte[] sosPayload = new byte[payloadLen];
                    ReadExact(s, sosPayload, payloadLen);
                    _segments.Add(new SegmentRef { Marker = marker, Payload = sosPayload, OriginalTotalLength = 4 + payloadLen });
                    _entropyDataOffset = payloadStart + payloadLen;
                    return;
                }

                // SOFn markers (baseline/progressive/etc. start-of-frame) carry the actual pixel
                // width/height. They're tiny (~8-20 bytes) so we cache them too, even though they
                // aren't "metadata" in the EXIF/JFIF sense.
                bool isSof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                bool isMetadataMarker = marker is 0xE0 or 0xE1 or 0xED or 0xFE || isSof;
                var seg = new SegmentRef { Marker = marker, OriginalTotalLength = 4 + payloadLen };

                if (isMetadataMarker)
                {
                    byte[] payload = new byte[payloadLen];
                    ReadExact(s, payload, payloadLen);
                    seg.Payload = payload;
                }
                else
                {
                    // Not something we parse (DQT/DHT/DRI/other APPn/etc): remember where it lives
                    // on disk and skip straight past it without reading its bytes.
                    seg.SourceOffset = payloadStart;
                    seg.SourceLength = payloadLen;
                    s.Seek(payloadStart + payloadLen, SeekOrigin.Begin);
                }

                _segments.Add(seg);
                int segIndex = _segments.Count - 1;

                if (isMetadataMarker)
                {
                    if (isSof)
                    {
                        // SOF payload: precision(1) + height(2) + width(2) + numComponents(1) + ...
                        if (seg.Payload!.Length >= 5)
                        {
                            _imageHeight = BinaryPrimitives.ReadUInt16BigEndian(seg.Payload.AsSpan(1));
                            _imageWidth = BinaryPrimitives.ReadUInt16BigEndian(seg.Payload.AsSpan(3));
                        }
                    }
                    else switch (marker)
                        {
                            case 0xE0:
                                if (StartsWith(seg.Payload!, JfifId)) _jfifPayload = seg.Payload;
                                break;
                            case 0xE1:
                                if (StartsWith(seg.Payload!, ExifId) && !_hasExif)
                                {
                                    ParseExif(seg.Payload!, ExifId.Length);
                                    _hasExif = true;
                                    _exifSegmentIndex = segIndex;
                                }
                                else if (StartsWith(seg.Payload!, XmpId))
                                {
                                    _xmpXml = Encoding.UTF8.GetString(seg.Payload!, XmpId.Length, seg.Payload!.Length - XmpId.Length);
                                }
                                break;
                            case 0xED:
                                if (StartsWith(seg.Payload!, PhotoshopId)) _photoshopPayload = seg.Payload;
                                break;
                            case 0xFE:
                                _commentSegmentIndex = segIndex;
                                break;
                        }
                }
            }
        }

        private static void ReadExact(Stream s, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buffer, total, count - total);
                if (n == 0) throw new EndOfStreamException("Unexpected end of stream.");
                total += n;
            }
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (data[i] != prefix[i]) return false;
            return true;
        }

        #endregion

        #region EXIF parsing

        private void ParseExif(byte[] app1, int tiffStart)
        {
            bool little;
            if (app1[tiffStart] == 0x49 && app1[tiffStart + 1] == 0x49) little = true;       // "II"
            else if (app1[tiffStart] == 0x4D && app1[tiffStart + 1] == 0x4D) little = false; // "MM"
            else throw new InvalidDataException("Invalid TIFF byte-order marker in EXIF block.");

            byte[] tiff = new byte[app1.Length - tiffStart];
            Array.Copy(app1, tiffStart, tiff, 0, tiff.Length);

            int ifd0Offset = (int)ReadU32(tiff, 4, little);
            Ifd0Entries = ParseIfd(tiff, ifd0Offset, little, out _);

            var exifPtr = Ifd0Entries.FirstOrDefault(e => e.Tag == ExifTags.ExifIfdPointer);
            if (exifPtr != null)
            {
                int off = (int)BinaryPrimitives.ReadUInt32BigEndian(exifPtr.ValueBytes);
                ExifSubIfdEntries = ParseIfd(tiff, off, little, out _);
                Ifd0Entries.Remove(exifPtr); // structural pointer, regenerated automatically on Save()
            }

            var gpsPtr = Ifd0Entries.FirstOrDefault(e => e.Tag == ExifTags.GpsIfdPointer);
            if (gpsPtr != null)
            {
                int off = (int)BinaryPrimitives.ReadUInt32BigEndian(gpsPtr.ValueBytes);
                GpsIfdEntries = ParseIfd(tiff, off, little, out _);
                Ifd0Entries.Remove(gpsPtr);
            }
        }

        private static List<IfdEntry> ParseIfd(byte[] tiff, int ifdOffset, bool little, out int nextIfdOffset)
        {
            var list = new List<IfdEntry>();
            if (ifdOffset <= 0 || ifdOffset + 2 > tiff.Length) { nextIfdOffset = 0; return list; }

            int count = ReadU16(tiff, ifdOffset, little);
            int p = ifdOffset + 2;
            for (int i = 0; i < count; i++)
            {
                if (p + 12 > tiff.Length) break;
                ushort tag = (ushort)ReadU16(tiff, p, little);
                ushort typeRaw = (ushort)ReadU16(tiff, p + 2, little);
                var type = Enum.IsDefined(typeof(ExifDataType), typeRaw) ? (ExifDataType)typeRaw : ExifDataType.Undefined;
                uint cnt = ReadU32(tiff, p + 4, little);
                int size = TypeSize(type) * (int)cnt;
                if (size < 0) size = 0;

                byte[] raw = new byte[size];
                if (size <= 4)
                {
                    Array.Copy(tiff, p + 8, raw, 0, size);
                }
                else
                {
                    uint off = ReadU32(tiff, p + 8, little);
                    if (off + size <= tiff.Length)
                        Array.Copy(tiff, (int)off, raw, 0, size);
                }

                byte[] bigEndianRaw = little ? SwapToBigEndian(raw, type, cnt) : raw;
                list.Add(new IfdEntry { Tag = tag, Type = type, Count = cnt, ValueBytes = bigEndianRaw });
                p += 12;
            }
            nextIfdOffset = p + 4 <= tiff.Length ? (int)ReadU32(tiff, p, little) : 0;
            return list;
        }

        private static byte[] SwapToBigEndian(byte[] data, ExifDataType type, uint count)
        {
            int elemSize = TypeSize(type);
            if (elemSize <= 1) return data;

            byte[] result = new byte[data.Length];
            int stride = elemSize;
            bool isRational = type is ExifDataType.Rational or ExifDataType.SRational;
            for (int i = 0; i + stride <= data.Length; i += stride)
            {
                if (!isRational)
                {
                    for (int b = 0; b < elemSize; b++)
                        result[i + b] = data[i + elemSize - 1 - b];
                }
                else
                {
                    for (int half = 0; half < 2; half++)
                        for (int b = 0; b < 4; b++)
                            result[i + half * 4 + b] = data[i + half * 4 + (3 - b)];
                }
            }
            return result;
        }

        internal static int TypeSize(ExifDataType t) => t switch
        {
            ExifDataType.Byte or ExifDataType.Ascii or ExifDataType.SByte or ExifDataType.Undefined => 1,
            ExifDataType.Short or ExifDataType.SShort => 2,
            ExifDataType.Long or ExifDataType.SLong or ExifDataType.Float => 4,
            ExifDataType.Rational or ExifDataType.SRational or ExifDataType.Double => 8,
            _ => 1
        };

        private static int ReadU16(byte[] b, int off, bool little) =>
            little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(off)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(off));

        private static uint ReadU32(byte[] b, int off, bool little) =>
            little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off));

        #endregion

        #region Listing all metadata

        public List<MetadataEntry> GetAllMetadata()
        {
            var result = new List<MetadataEntry>();

            if (_jfifPayload is { } jfif && jfif.Length >= 14)
            {
                result.Add(Simple(MetadataGroup.Jfif, "JFIF Version", $"{jfif[5]}.{jfif[6]:00}"));
                result.Add(Simple(MetadataGroup.Jfif, "Density Units", jfif[7] switch { 0 => "aspect ratio", 1 => "pixels/inch", 2 => "pixels/cm", _ => "unknown" }));
                result.Add(Simple(MetadataGroup.Jfif, "X Density", BinaryPrimitives.ReadUInt16BigEndian(jfif.AsSpan(8))));
                result.Add(Simple(MetadataGroup.Jfif, "Y Density", BinaryPrimitives.ReadUInt16BigEndian(jfif.AsSpan(10))));
                result.Add(Simple(MetadataGroup.Jfif, "Thumbnail Size", $"{jfif[12]}x{jfif[13]}"));
            }

            foreach (var e in Ifd0Entries) result.Add(ToMetadataEntry(e, MetadataGroup.ExifIfd0));
            foreach (var e in ExifSubIfdEntries) result.Add(ToMetadataEntry(e, MetadataGroup.ExifSubIfd));
            foreach (var e in GpsIfdEntries) result.Add(ToMetadataEntry(e, MetadataGroup.ExifGps));

            if (_photoshopPayload is { } ps)
                result.AddRange(ParseIptc(ps));

            if (_xmpXml is { } xmp)
                result.Add(Simple(MetadataGroup.Xmp, "XMP Packet", xmp.Length <= 300 ? xmp : xmp.Substring(0, 300) + "... (truncated)"));

            string? comment = GetComment();
            if (comment != null)
                result.Add(Simple(MetadataGroup.Comment, "Comment", comment));

            return result;
        }

        private static MetadataEntry Simple(MetadataGroup g, string name, object value) => new()
        {
            Group = g,
            Name = name,
            DataType = ExifDataType.Ascii,
            Count = 1,
            Value = value
        };

        private static MetadataEntry ToMetadataEntry(IfdEntry e, MetadataGroup group) => new()
        {
            Group = group,
            TagId = e.Tag,
            Name = TagNames.TryGetValue(e.Tag, out var n) ? n : $"Unknown Tag 0x{e.Tag:X4}",
            DataType = e.Type,
            Count = (int)e.Count,
            Value = DecodeValue(e)
        };

        private static object? DecodeValue(IfdEntry e)
        {
            byte[] v = e.ValueBytes;
            switch (e.Type)
            {
                case ExifDataType.Ascii:
                    return Encoding.ASCII.GetString(v).TrimEnd('\0');
                case ExifDataType.Byte:
                case ExifDataType.Undefined:
                    return e.Count == 1 ? v.Length > 0 ? v[0] : (object)0 : v;
                case ExifDataType.SByte:
                    return v.Select(b => (sbyte)b).ToArray();
                case ExifDataType.Short:
                    {
                        var arr = ReadArray(v, 2, BinaryPrimitives.ReadUInt16BigEndian);
                        return arr.Length == 1 ? arr[0] : (object)arr;
                    }
                case ExifDataType.SShort:
                    {
                        var arr = ReadArray(v, 2, BinaryPrimitives.ReadInt16BigEndian);
                        return arr.Length == 1 ? arr[0] : (object)arr;
                    }
                case ExifDataType.Long:
                    {
                        var arr = ReadArray(v, 4, BinaryPrimitives.ReadUInt32BigEndian);
                        return arr.Length == 1 ? arr[0] : (object)arr;
                    }
                case ExifDataType.SLong:
                    {
                        var arr = ReadArray(v, 4, BinaryPrimitives.ReadInt32BigEndian);
                        return arr.Length == 1 ? arr[0] : (object)arr;
                    }
                case ExifDataType.Float:
                    {
                        var arr = ReadArray(v, 4, BinaryPrimitives.ReadSingleBigEndian);
                        return arr.Length == 1 ? arr[0] : (object)arr;
                    }
                case ExifDataType.Double:
                    {
                        var arr = ReadArray(v, 8, BinaryPrimitives.ReadDoubleBigEndian);
                        return arr.Length == 1 ? arr[0] : (object)arr;
                    }
                case ExifDataType.Rational:
                    {
                        var arr = new List<URational>();
                        for (int i = 0; i + 8 <= v.Length; i += 8)
                            arr.Add(new URational(BinaryPrimitives.ReadUInt32BigEndian(v.AsSpan(i)), BinaryPrimitives.ReadUInt32BigEndian(v.AsSpan(i + 4))));
                        return arr.Count == 1 ? arr[0] : (object)arr.ToArray();
                    }
                case ExifDataType.SRational:
                    {
                        var arr = new List<SRational>();
                        for (int i = 0; i + 8 <= v.Length; i += 8)
                            arr.Add(new SRational(BinaryPrimitives.ReadInt32BigEndian(v.AsSpan(i)), BinaryPrimitives.ReadInt32BigEndian(v.AsSpan(i + 4))));
                        return arr.Count == 1 ? arr[0] : (object)arr.ToArray();
                    }
                default:
                    return v;
            }
        }

        private static T[] ReadArray<T>(byte[] v, int elemSize, Func<ReadOnlySpan<byte>, T> reader)
        {
            int n = v.Length / elemSize;
            var result = new T[n];
            for (int i = 0; i < n; i++) result[i] = reader(v.AsSpan(i * elemSize, elemSize));
            return result;
        }

        private static readonly Dictionary<int, string> TagNames = new()
        {
            [ExifTags.ImageDescription] = "Image Description",
            [ExifTags.Make] = "Make",
            [ExifTags.Model] = "Model",
            [ExifTags.Orientation] = "Orientation",
            [ExifTags.XResolution] = "X Resolution",
            [ExifTags.YResolution] = "Y Resolution",
            [ExifTags.ResolutionUnit] = "Resolution Unit",
            [ExifTags.Software] = "Software",
            [ExifTags.DateTime] = "Date/Time",
            [ExifTags.Artist] = "Artist",
            [ExifTags.Copyright] = "Copyright",
            [ExifTags.ExposureTime] = "Exposure Time",
            [ExifTags.FNumber] = "F-Number",
            [ExifTags.ExposureProgram] = "Exposure Program",
            [ExifTags.IsoSpeedRatings] = "ISO Speed",
            [ExifTags.ExifVersion] = "Exif Version",
            [ExifTags.DateTimeOriginal] = "Date/Time Original",
            [ExifTags.DateTimeDigitized] = "Date/Time Digitized",
            [ExifTags.FocalLength] = "Focal Length",
            [ExifTags.Flash] = "Flash",
            [ExifTags.PixelXDimension] = "Pixel X Dimension",
            [ExifTags.PixelYDimension] = "Pixel Y Dimension",
            [ExifTags.ExposureMode] = "Exposure Mode",
            [ExifTags.WhiteBalance] = "White Balance",
            [ExifTags.FocalLengthIn35mmFilm] = "Focal Length (35mm equiv.)",
            [ExifTags.SceneCaptureType] = "Scene Capture Type",
            [ExifTags.LensModel] = "Lens Model",
            [ExifTags.GPSLatitudeRef] = "GPS Latitude Ref",
            [ExifTags.GPSLatitude] = "GPS Latitude",
            [ExifTags.GPSLongitudeRef] = "GPS Longitude Ref",
            [ExifTags.GPSLongitude] = "GPS Longitude",
            [ExifTags.GPSAltitudeRef] = "GPS Altitude Ref",
            [ExifTags.GPSAltitude] = "GPS Altitude",
            [ExifTags.GPSTimeStamp] = "GPS Time Stamp",
            [ExifTags.GPSDateStamp] = "GPS Date Stamp",
        };

        private static readonly Dictionary<int, string> IptcDatasetNames = new()
        {
            [5] = "Object Name",
            [15] = "Category",
            [25] = "Keywords",
            [40] = "Special Instructions",
            [55] = "Date Created",
            [80] = "By-line",
            [85] = "By-line Title",
            [90] = "City",
            [95] = "Province/State",
            [101] = "Country",
            [103] = "Original Transmission Reference",
            [105] = "Headline",
            [110] = "Credit",
            [115] = "Source",
            [116] = "Copyright Notice",
            [120] = "Caption/Abstract",
            [122] = "Writer/Editor"
        };

        private static List<MetadataEntry> ParseIptc(byte[] photoshop)
        {
            var results = new List<MetadataEntry>();
            int p = PhotoshopId.Length;
            while (p + 12 <= photoshop.Length)
            {
                if (photoshop[p] != 0x38 || photoshop[p + 1] != 0x42 || photoshop[p + 2] != 0x49 || photoshop[p + 3] != 0x4D) break; // "8BIM"
                int resourceId = (photoshop[p + 4] << 8) | photoshop[p + 5];
                int nameLen = photoshop[p + 6];
                int nameBlockLen = nameLen + 1;
                if (nameBlockLen % 2 != 0) nameBlockLen++;
                int sizeOffset = p + 6 + nameBlockLen;
                if (sizeOffset + 4 > photoshop.Length) break;
                uint dataSize = BinaryPrimitives.ReadUInt32BigEndian(photoshop.AsSpan(sizeOffset));
                int dataOffset = sizeOffset + 4;
                if (dataOffset + dataSize > photoshop.Length) break;

                if (resourceId == 0x0404)
                {
                    int ip = dataOffset;
                    int end = dataOffset + (int)dataSize;
                    while (ip + 5 <= end)
                    {
                        if (photoshop[ip] != 0x1C) break;
                        int record = photoshop[ip + 1];
                        int dataset = photoshop[ip + 2];
                        int len = (photoshop[ip + 3] << 8) | photoshop[ip + 4];
                        ip += 5;
                        if (ip + len > end) break;
                        if (record == 2)
                        {
                            string text = Encoding.UTF8.GetString(photoshop, ip, len);
                            string name = IptcDatasetNames.TryGetValue(dataset, out var n) ? n : $"IPTC Dataset 2:{dataset}";
                            results.Add(new MetadataEntry { Group = MetadataGroup.Iptc, TagId = dataset, Name = name, DataType = ExifDataType.Ascii, Count = len, Value = text });
                        }
                        ip += len;
                    }
                }

                int padded = (int)dataSize;
                if (padded % 2 != 0) padded++;
                p = dataOffset + padded;
            }
            return results;
        }

        #endregion

        #region Reading a single tag

        public MetadataEntry? GetExifEntry(int tagId, IfdTarget target = IfdTarget.Ifd0)
        {
            var list = TargetList(target);
            var e = list.FirstOrDefault(x => x.Tag == tagId);
            return e == null ? null : ToMetadataEntry(e, target switch
            {
                IfdTarget.ExifSubIfd => MetadataGroup.ExifSubIfd,
                IfdTarget.Gps => MetadataGroup.ExifGps,
                _ => MetadataGroup.ExifIfd0
            });
        }

        /// <summary>Max payload a single JPEG segment can hold: 0xFFFF (2-byte length field max)
        /// minus the 2 length bytes themselves.</summary>
        public const int MaxCommentBytes = 65533;

        public string? GetComment()
        {
            byte[]? bytes = GetCommentBytes();
            return bytes == null ? null : Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Reads the raw bytes of the comment segment, with no text decoding applied.
        /// Null if there is no comment segment.</summary>
        public byte[]? GetCommentBytes()
        {
            if (_commentSegmentIndex < 0 || _commentSegmentIndex >= _segments.Count) return null;
            return _segments[_commentSegmentIndex].Payload ?? Array.Empty<byte>();
        }

        #endregion

        #region Friendly convenience attributes

        /// <summary>Actual pixel width/height decoded from the JPEG's own SOF marker
        /// (not from EXIF, so it's accurate even if EXIF PixelXDimension/PixelYDimension is stale
        /// or absent). Null if no SOF marker was found (malformed/truncated file).</summary>
        public (int Width, int Height)? PixelDimensions =>
            _imageWidth.HasValue && _imageHeight.HasValue ? (_imageWidth.Value, _imageHeight.Value) : null;

        /// <summary>When the photo was taken, read from EXIF DateTimeOriginal, falling back to
        /// DateTimeDigitized, then to the IFD0 DateTime (file modified date). Null if none present
        /// or unparseable.</summary>
        public DateTime? DateTaken
        {
            get
            {
                string? raw = GetExifAsciiValue(ExifTags.DateTimeOriginal, IfdTarget.ExifSubIfd)
                    ?? GetExifAsciiValue(ExifTags.DateTimeDigitized, IfdTarget.ExifSubIfd)
                    ?? GetExifAsciiValue(ExifTags.DateTime, IfdTarget.Ifd0);
                if (string.IsNullOrEmpty(raw)) return null;
                // EXIF date format is fixed-width: "yyyy:MM:dd HH:mm:ss"
                return DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                    ? dt : null;
            }
        }

        /// <summary>DPI-style resolution from IFD0 XResolution/YResolution + ResolutionUnit.
        /// This is a print/display hint, unrelated to pixel dimensions. Null if not present.</summary>
        public (double X, double Y, string Unit)? DpiResolution
        {
            get
            {
                var xEntry = Ifd0Entries.FirstOrDefault(e => e.Tag == ExifTags.XResolution);
                var yEntry = Ifd0Entries.FirstOrDefault(e => e.Tag == ExifTags.YResolution);
                if (xEntry == null || yEntry == null) return null;

                double x = RationalToDouble(xEntry.ValueBytes);
                double y = RationalToDouble(yEntry.ValueBytes);

                var unitEntry = Ifd0Entries.FirstOrDefault(e => e.Tag == ExifTags.ResolutionUnit);
                string unit = "inch"; // EXIF default when the tag is absent
                if (unitEntry != null && unitEntry.ValueBytes.Length >= 2)
                {
                    unit = BinaryPrimitives.ReadUInt16BigEndian(unitEntry.ValueBytes) switch
                    {
                        2 => "inch",
                        3 => "cm",
                        _ => "none"
                    };
                }
                return (x, y, unit);
            }
        }

        public string? CameraMake => GetExifAsciiValue(ExifTags.Make, IfdTarget.Ifd0);
        public string? CameraModel => GetExifAsciiValue(ExifTags.Model, IfdTarget.Ifd0);
        public string? LensModel => GetExifAsciiValue(ExifTags.LensModel, IfdTarget.ExifSubIfd);
        public string? Software => GetExifAsciiValue(ExifTags.Software, IfdTarget.Ifd0);

        /// <summary>Raw EXIF orientation value (1-8). See ExifOrientationDescription for a human label.</summary>
        public int? Orientation => GetExifUShortValue(ExifTags.Orientation, IfdTarget.Ifd0);

        public string? ExifOrientationDescription => Orientation switch
        {
            1 => "Normal",
            2 => "Flipped horizontally",
            3 => "Rotated 180°",
            4 => "Flipped vertically",
            5 => "Rotated 90° CW + flipped",
            6 => "Rotated 90° CW",
            7 => "Rotated 90° CCW + flipped",
            8 => "Rotated 90° CCW",
            _ => null
        };

        /// <summary>Exposure time as a double (seconds), e.g. 0.004 for a 1/250s shutter speed.</summary>
        public double? ExposureTimeSeconds => GetExifRationalValue(ExifTags.ExposureTime, IfdTarget.ExifSubIfd);

        /// <summary>F-number (aperture), e.g. 2.8 for f/2.8.</summary>
        public double? FNumber => GetExifRationalValue(ExifTags.FNumber, IfdTarget.ExifSubIfd);

        /// <summary>Focal length in millimeters.</summary>
        public double? FocalLengthMm => GetExifRationalValue(ExifTags.FocalLength, IfdTarget.ExifSubIfd);

        public int? IsoSpeed => GetExifUShortValue(ExifTags.IsoSpeedRatings, IfdTarget.ExifSubIfd);

        private string? GetExifAsciiValue(int tagId, IfdTarget target)
        {
            var e = TargetList(target).FirstOrDefault(x => x.Tag == tagId);
            return e == null ? null : Encoding.ASCII.GetString(e.ValueBytes).TrimEnd('\0');
        }

        private int? GetExifUShortValue(int tagId, IfdTarget target)
        {
            var e = TargetList(target).FirstOrDefault(x => x.Tag == tagId);
            if (e == null || e.ValueBytes.Length < 2) return null;
            return BinaryPrimitives.ReadUInt16BigEndian(e.ValueBytes);
        }

        private double? GetExifRationalValue(int tagId, IfdTarget target)
        {
            var e = TargetList(target).FirstOrDefault(x => x.Tag == tagId);
            if (e == null || e.ValueBytes.Length < 8) return null;
            return RationalToDouble(e.ValueBytes);
        }

        private static double RationalToDouble(byte[] v)
        {
            if (v.Length < 8) return 0;
            uint num = BinaryPrimitives.ReadUInt32BigEndian(v.AsSpan(0));
            uint den = BinaryPrimitives.ReadUInt32BigEndian(v.AsSpan(4));
            return den == 0 ? 0 : (double)num / den;
        }

        #endregion

        #region Writing tags

        private List<IfdEntry> TargetList(IfdTarget target) => target switch
        {
            IfdTarget.ExifSubIfd => ExifSubIfdEntries,
            IfdTarget.Gps => GpsIfdEntries,
            _ => Ifd0Entries
        };

        public void SetExifValue(int tagId, ExifDataType type, uint count, byte[] bigEndianValueBytes, IfdTarget target = IfdTarget.Ifd0)
        {
            var list = TargetList(target);
            var existing = list.FirstOrDefault(e => e.Tag == tagId);
            if (existing != null)
            {
                existing.Type = type;
                existing.Count = count;
                existing.ValueBytes = bigEndianValueBytes;
            }
            else
            {
                list.Add(new IfdEntry { Tag = (ushort)tagId, Type = type, Count = count, ValueBytes = bigEndianValueBytes });
            }
        }

        public void SetExifAscii(int tagId, string value, IfdTarget target = IfdTarget.Ifd0)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value + "\0");
            SetExifValue(tagId, ExifDataType.Ascii, (uint)bytes.Length, bytes, target);
        }

        public void SetExifShort(int tagId, ushort value, IfdTarget target = IfdTarget.Ifd0)
        {
            byte[] bytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            SetExifValue(tagId, ExifDataType.Short, 1, bytes, target);
        }

        public void SetExifLong(int tagId, uint value, IfdTarget target = IfdTarget.Ifd0)
        {
            byte[] bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            SetExifValue(tagId, ExifDataType.Long, 1, bytes, target);
        }

        public void SetExifRational(int tagId, uint numerator, uint denominator, IfdTarget target = IfdTarget.Ifd0)
        {
            byte[] bytes = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), numerator);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), denominator);
            SetExifValue(tagId, ExifDataType.Rational, 1, bytes, target);
        }

        public void RemoveExifTag(int tagId, IfdTarget target = IfdTarget.Ifd0)
        {
            TargetList(target).RemoveAll(e => e.Tag == tagId);
        }

        public void SetComment(string text) => SetCommentBytes(Encoding.UTF8.GetBytes(text));

        /// <summary>Stores raw bytes in the COM segment (not interpreted as text). Throws if the
        /// data exceeds what a single JPEG segment can hold (see MaxCommentBytes).</summary>
        public void SetCommentBytes(byte[] data)
        {
            if (data is null) throw new ArgumentNullException(nameof(data));
            if (data.Length > MaxCommentBytes)
                throw new ArgumentException(
                    $"Comment data is {data.Length} bytes, which exceeds the {MaxCommentBytes}-byte limit " +
                    "for a single JPEG COM segment.", nameof(data));

            if (_commentSegmentIndex >= 0 && _commentSegmentIndex < _segments.Count)
            {
                _segments[_commentSegmentIndex].Payload = data;
            }
            else
            {
                var seg = new SegmentRef { Marker = 0xFE, Payload = data };
                int insertAt = 0;
                while (insertAt < _segments.Count && _segments[insertAt].Marker is 0xE0 or 0xE1) insertAt++;
                _segments.Insert(insertAt, seg);
                _commentSegmentIndex = insertAt;
                if (_exifSegmentIndex >= insertAt) _exifSegmentIndex++;
            }
        }

        public void RemoveComment()
        {
            if (_commentSegmentIndex < 0 || _commentSegmentIndex >= _segments.Count) return;

            var seg = _segments[_commentSegmentIndex];
            if (seg.OriginalTotalLength >= 0)
            {
                // It existed on disk: keep it in the list (marked Deleted) so SaveInPlaceBuffered
                // still correctly accounts for/skips its original bytes. Save(Stream) simply omits
                // writing anything for Deleted segments.
                seg.Deleted = true;
                seg.Payload = null;
                _commentSegmentIndex = -1;
            }
            else
            {
                // Never existed on disk (added this session, not yet saved) — safe to fully remove.
                _segments.RemoveAt(_commentSegmentIndex);
                if (_exifSegmentIndex > _commentSegmentIndex) _exifSegmentIndex--;
                _commentSegmentIndex = -1;
            }
        }

        #endregion

        #region Save (streaming — untouched segments and image data are copied, not buffered)

        public void Save(string path)
        {
            using var fs = File.Create(path);
            Save(fs);
        }

        public void Save(Stream destination)
        {
            if (_source is null)
                throw new InvalidOperationException("No source loaded, or the source stream was already closed. Call Load() first and keep it open until Save() completes.");

            SyncExifSegment();

            destination.WriteByte(0xFF);
            destination.WriteByte(0xD8);

            foreach (var seg in _segments)
            {
                if (seg.Deleted) continue;

                if (seg.Marker == 0x01 || (seg.Marker >= 0xD0 && seg.Marker <= 0xD7))
                {
                    destination.WriteByte(0xFF);
                    destination.WriteByte(seg.Marker);
                    continue;
                }

                destination.WriteByte(0xFF);
                destination.WriteByte(seg.Marker);

                if (seg.Payload != null)
                {
                    int len = seg.Payload.Length + 2;
                    destination.WriteByte((byte)(len >> 8));
                    destination.WriteByte((byte)(len & 0xFF));
                    destination.Write(seg.Payload, 0, seg.Payload.Length);
                }
                else
                {
                    int len = seg.SourceLength + 2;
                    destination.WriteByte((byte)(len >> 8));
                    destination.WriteByte((byte)(len & 0xFF));
                    StreamCopyRange(_source, destination, seg.SourceOffset, seg.SourceLength);
                }
            }

            // Everything from here on (entropy-coded image data + EOI) is streamed straight
            // from the source file to the destination in small chunks — never buffered whole.
            _source.Seek(_entropyDataOffset, SeekOrigin.Begin);
            _source.CopyTo(destination);
        }

        /// <summary>Writes the changes back to the very file this object was loaded from
        /// (via Load(string path)). Safe to call even though the source is still open for
        /// reading: writes go to a temp file in the same directory first, the read handle on
        /// the original is released, then the temp file atomically replaces it. The object is
        /// reloaded from the new file afterward and remains usable.</summary>
        public void SaveInPlace()
        {
            if (_sourcePath is null)
                throw new InvalidOperationException(
                    "SaveInPlace() requires the file to have been opened with Load(string path). " +
                    "Streams opened via Load(Stream) don't have a known file path — use Save(Stream) instead.");
            if (_source is null)
                throw new InvalidOperationException("The source is closed; nothing to save.");

            string fullPath = Path.GetFullPath(_sourcePath);
            string dir = Path.GetDirectoryName(fullPath) ?? ".";
            string tempPath = Path.Combine(dir, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    Save(tempStream); // still reads untouched segments/image data from the original, read-only
                }

                Close(); // release the read handle on the original file before replacing it

                File.Move(tempPath, fullPath, overwrite: true); // atomic within the same directory/volume
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
                }
                throw;
            }

            // reflect what's now actually on disk, and keep the object usable for further edits
            Load(fullPath);
        }

        /// <summary>Writes the changes back to the same file using a single file handle and a
        /// flexible-size lookahead buffer (InPlaceRewriteStream) instead of a temp file — no second
        /// copy of the file is ever created on disk. Only requires Load(string path).</summary>
        public void SaveInPlaceBuffered()
        {
            if (_sourcePath is null)
                throw new InvalidOperationException(
                    "SaveInPlaceBuffered() requires the file to have been opened with Load(string path).");
            if (_source is null)
                throw new InvalidOperationException("The source is closed; nothing to save.");

            SyncExifSegment();

            string fullPath = Path.GetFullPath(_sourcePath);
            Close(); // release our read-only handle; InPlaceRewriteStream opens its own ReadWrite handle

            using (var rw = new InPlaceRewriteStream(fullPath))
            {
                // SOI: always present, always the same 2 bytes, but still occupies original space
                rw.SkipSource(2);
                rw.WriteNew(new byte[] { 0xFF, 0xD8 });

                foreach (var seg in _segments)
                {
                    // RST/TEM markers (2 bytes, no payload) — always came from the original file
                    if (seg.Marker == 0x01 || (seg.Marker >= 0xD0 && seg.Marker <= 0xD7))
                    {
                        if (seg.OriginalTotalLength >= 0) rw.SkipSource(seg.OriginalTotalLength);
                        if (!seg.Deleted) rw.WriteNew(new byte[] { 0xFF, seg.Marker });
                        continue;
                    }

                    if (seg.OriginalTotalLength >= 0)
                    {
                        // This slot existed on disk — its original bytes must be accounted for
                        // (skipped or copied) regardless of whether we're replacing or deleting it.
                        if (seg.Payload != null)
                        {
                            // Cached segment (JFIF/EXIF/IPTC/XMP/COM/SOS): entire original span is
                            // superseded by whatever we hold in memory now.
                            rw.SkipSource(seg.OriginalTotalLength);
                            if (!seg.Deleted)
                            {
                                int len = seg.Payload.Length + 2;
                                rw.WriteNew(new byte[] { 0xFF, seg.Marker, (byte)(len >> 8), (byte)(len & 0xFF) });
                                rw.WriteNew(seg.Payload);
                            }
                        }
                        else
                        {
                            // Passthrough segment (DQT/SOF/DHT/etc.): header re-emitted, payload
                            // copied through verbatim from disk (never fully buffered in memory).
                            rw.SkipSource(4); // original 0xFF + marker + 2 length bytes
                            int len = seg.SourceLength + 2;
                            rw.WriteNew(new byte[] { 0xFF, seg.Marker, (byte)(len >> 8), (byte)(len & 0xFF) });
                            rw.CopyThrough(seg.SourceLength);
                        }
                    }
                    else
                    {
                        // Brand new segment added this session (e.g. EXIF/Comment added where none
                        // existed before) — nothing to skip, it has no original on-disk counterpart.
                        int len = seg.Payload!.Length + 2;
                        rw.WriteNew(new byte[] { 0xFF, seg.Marker, (byte)(len >> 8), (byte)(len & 0xFF) });
                        rw.WriteNew(seg.Payload);
                    }
                }

                rw.CopyThroughRemaining(); // entropy-coded image data + EOI, streamed straight through
                rw.Finish();
            }

            Load(fullPath); // reopen so the object reflects what's now on disk
        }

        private static void StreamCopyRange(Stream source, Stream destination, long offset, int length)
        {
            source.Seek(offset, SeekOrigin.Begin);
            byte[] buffer = new byte[Math.Min(65536, Math.Max(1, length))];
            int remaining = length;
            while (remaining > 0)
            {
                int n = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (n == 0) throw new EndOfStreamException("Source stream ended before expected segment length.");
                destination.Write(buffer, 0, n);
                remaining -= n;
            }
        }

        private void SyncExifSegment()
        {
            bool needsExif = Ifd0Entries.Count > 0 || ExifSubIfdEntries.Count > 0 || GpsIfdEntries.Count > 0;

            if (!needsExif)
            {
                if (_exifSegmentIndex >= 0 && _exifSegmentIndex < _segments.Count)
                {
                    var seg = _segments[_exifSegmentIndex];
                    if (seg.OriginalTotalLength >= 0)
                    {
                        seg.Deleted = true;
                        seg.Payload = null;
                        _exifSegmentIndex = -1;
                    }
                    else
                    {
                        _segments.RemoveAt(_exifSegmentIndex);
                        if (_commentSegmentIndex > _exifSegmentIndex) _commentSegmentIndex--;
                        _exifSegmentIndex = -1;
                    }
                }
                return;
            }

            byte[] tiff = BuildTiff();
            byte[] payload = new byte[ExifId.Length + tiff.Length];
            Array.Copy(ExifId, payload, ExifId.Length);
            Array.Copy(tiff, 0, payload, ExifId.Length, tiff.Length);

            if (_exifSegmentIndex >= 0 && _exifSegmentIndex < _segments.Count)
            {
                _segments[_exifSegmentIndex].Payload = payload;
            }
            else
            {
                var seg = new SegmentRef { Marker = 0xE1, Payload = payload };
                int insertAt = 0;
                if (_segments.Count > 0 && _segments[0].Marker == 0xE0) insertAt = 1;
                _segments.Insert(insertAt, seg);
                _exifSegmentIndex = insertAt;
                if (_commentSegmentIndex >= insertAt) _commentSegmentIndex++;
            }
        }

        private byte[] BuildTiff()
        {
            var ifd0 = new List<IfdEntry>(Ifd0Entries);
            bool hasExifSub = ExifSubIfdEntries.Count > 0;
            bool hasGps = GpsIfdEntries.Count > 0;

            IfdEntry? exifPtr = null, gpsPtr = null;
            if (hasExifSub)
            {
                exifPtr = new IfdEntry { Tag = ExifTags.ExifIfdPointer, Type = ExifDataType.Long, Count = 1, ValueBytes = new byte[4] };
                ifd0.Add(exifPtr);
            }
            if (hasGps)
            {
                gpsPtr = new IfdEntry { Tag = ExifTags.GpsIfdPointer, Type = ExifDataType.Long, Count = 1, ValueBytes = new byte[4] };
                ifd0.Add(gpsPtr);
            }

            ifd0.Sort((a, b) => a.Tag.CompareTo(b.Tag));
            var exifSub = new List<IfdEntry>(ExifSubIfdEntries);
            exifSub.Sort((a, b) => a.Tag.CompareTo(b.Tag));
            var gps = new List<IfdEntry>(GpsIfdEntries);
            gps.Sort((a, b) => a.Tag.CompareTo(b.Tag));

            const int headerSize = 8;
            int ifd0Start = headerSize;
            int ifd0TableSize = TableSize(ifd0.Count);
            int externalCursor = ifd0Start + ifd0TableSize + ComputeExternalSize(ifd0);

            int exifSubStart = 0, gpsStart = 0;
            if (hasExifSub)
            {
                exifSubStart = externalCursor;
                BinaryPrimitives.WriteUInt32BigEndian(exifPtr!.ValueBytes, (uint)exifSubStart);
                externalCursor = exifSubStart + TableSize(exifSub.Count) + ComputeExternalSize(exifSub);
            }
            if (hasGps)
            {
                gpsStart = externalCursor;
                BinaryPrimitives.WriteUInt32BigEndian(gpsPtr!.ValueBytes, (uint)gpsStart);
            }

            var buffer = new List<byte>(externalCursor + 64);
            buffer.Add(0x4D); buffer.Add(0x4D);
            buffer.Add(0x00); buffer.Add(0x2A);
            AppendU32BE(buffer, (uint)ifd0Start);

            WriteIfd(ifd0, buffer);
            if (hasExifSub) WriteIfd(exifSub, buffer);
            if (hasGps) WriteIfd(gps, buffer);

            return buffer.ToArray();
        }

        private static int TableSize(int entryCount) => 2 + entryCount * 12 + 4;

        private static int ComputeExternalSize(List<IfdEntry> entries)
        {
            int total = 0;
            foreach (var e in entries)
                if (e.ValueBytes.Length > 4)
                    total += e.ValueBytes.Length + (e.ValueBytes.Length % 2);
            return total;
        }

        private static void WriteIfd(List<IfdEntry> entries, List<byte> buffer)
        {
            int startOffset = buffer.Count;
            int tableSize = TableSize(entries.Count);
            int externalOffset = startOffset + tableSize;

            AppendU16BE(buffer, (ushort)entries.Count);

            var offsets = new int[entries.Count];
            int cursor = externalOffset;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].ValueBytes.Length > 4)
                {
                    offsets[i] = cursor;
                    cursor += entries[i].ValueBytes.Length + (entries[i].ValueBytes.Length % 2);
                }
            }

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                AppendU16BE(buffer, e.Tag);
                AppendU16BE(buffer, (ushort)e.Type);
                AppendU32BE(buffer, e.Count);

                if (e.ValueBytes.Length <= 4)
                {
                    byte[] inline = new byte[4];
                    Array.Copy(e.ValueBytes, inline, e.ValueBytes.Length);
                    buffer.AddRange(inline);
                }
                else
                {
                    AppendU32BE(buffer, (uint)offsets[i]);
                }
            }

            AppendU32BE(buffer, 0); // next IFD offset (thumbnail IFD1 not supported)

            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].ValueBytes.Length > 4)
                {
                    buffer.AddRange(entries[i].ValueBytes);
                    if (entries[i].ValueBytes.Length % 2 != 0) buffer.Add(0);
                }
            }
        }

        private static void AppendU16BE(List<byte> buffer, ushort value)
        {
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)(value & 0xFF));
        }

        private static void AppendU32BE(List<byte> buffer, uint value)
        {
            buffer.Add((byte)(value >> 24));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)(value & 0xFF));
        }

        #endregion

        #region Disposal

        public void Close()
        {
            if (_ownsSource) _source?.Dispose();
            _source = null;
        }

        public void Dispose() => Close();

        #endregion
    }
}