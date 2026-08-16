// InPlaceRewriteStream.cs
//
// Rewrites a file "in place" — same file handle for both reading the original content and
// writing the new content — using a flexible-size lookahead buffer to guarantee the write
// cursor never overtakes the read cursor. That's the danger with true in-place rewriting:
// if new content is longer than what it replaces, a naive write could clobber bytes further
// into the file that haven't been read yet, corrupting them before you get to them.
//
// Invariant maintained at all times: writePosition <= readPosition, where readPosition is how
// far into the *original* file content has been pulled into memory so far. Before physically
// writing N bytes at the current write position, the buffer guarantees readPosition already
// covers at least writePosition+N — growing itself and reading further ahead if it doesn't.
// Only once that's true is it safe to overwrite those disk bytes: whatever was there has
// already been captured in memory (if it was still needed) or was explicitly skipped (if not).
//
// Two independent needs are handled by two different checks:
//   - CopyThrough/SkipSource need enough *source* bytes pulled into the buffer to act on
//     ("do I have N bytes to output/discard right now?").
//   - WriteNew (and CopyThrough's actual flush-to-disk step) need the *write-safety* margin
//     ("is it safe to physically overwrite the next N bytes on disk?").
// Conflating these two was an earlier bug in this class; they're now separate checks.
//
// This is a generic utility — not JPEG-specific — for any "read some, transform, write back
// to the same file" task where output size can differ from input size at various points.
//
// Typical usage (mirrors a segment-by-segment file rewrite):
//
//   using var rw = new InPlaceRewriteStream("photo.jpg");
//   rw.CopyThrough(insertionOffset);                 // unchanged leading bytes, copied verbatim
//   rw.SkipSource(oldExifSegmentLength);              // discard the old EXIF bytes (not written)
//   rw.WriteNew(newExifSegmentBytes);                 // write the replacement (can be longer or shorter)
//   rw.CopyThroughRemaining();                        // copy everything else (headers + image data) verbatim
//   rw.Finish();                                      // truncates if the result ended up shorter

using System;
using System.IO;

namespace MyApp.Models
{
    public sealed class InPlaceRewriteStream : IDisposable
    {
        private readonly FileStream _fs;
        private readonly long _originalLength;

        private byte[] _buf;
        private int _head;   // index into _buf of the first buffered-but-unconsumed byte
        private int _count;  // number of buffered-but-unconsumed bytes, starting at _head

        private long _readPos;  // file offset up to which original content has been pulled into the buffer
        private long _writePos; // file offset of the next byte to be physically written

        /// <summary>Current physical write cursor (file offset of the next byte to be written).</summary>
        public long WritePosition => _writePos;

        /// <summary>File offset of the next byte of *original* content not yet consumed
        /// (copied through, skipped, or otherwise accounted for).</summary>
        public long SourcePosition => _readPos - _count;

        /// <summary>How many bytes of the original file have not yet been consumed.</summary>
        public long SourceBytesRemaining => _originalLength - SourcePosition;

        public InPlaceRewriteStream(string path, int initialBufferSize = 64 * 1024)
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            _originalLength = _fs.Length;
            _buf = new byte[Math.Max(4096, initialBufferSize)];
        }

        /// <summary>Copies the next <paramref name="length"/> bytes of original content verbatim
        /// from the current source position to the current write position.</summary>
        public void CopyThrough(long length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            long remaining = length;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(remaining, _buf.Length);
                EnsureBuffered(chunk);
                int take = (int)Math.Min(remaining, _count);
                if (take == 0)
                    throw new EndOfStreamException("CopyThrough requested more bytes than remain in the source file.");

                EnsureWriteSafe(take); // guarantee it's safe to physically overwrite disk at the write cursor
                FlushBufferedBytes(take);
                remaining -= take;
            }
        }

        /// <summary>Copies every remaining unconsumed byte of the original file verbatim
        /// (typically called once at the end, after all edits have been applied).</summary>
        public void CopyThroughRemaining()
        {
            long remaining = SourceBytesRemaining;
            if (remaining > 0) CopyThrough(remaining);
        }

        /// <summary>Discards (does not write) the next <paramref name="length"/> bytes of original
        /// content — use this to "remove" or "replace" a region, then follow with WriteNew. This
        /// never needs to touch disk for the un-buffered portion: since the bytes are being thrown
        /// away, we simply advance past them without reading them at all.</summary>
        public void SkipSource(long length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            long remaining = length;

            int fromBuffer = (int)Math.Min(remaining, _count);
            _head += fromBuffer;
            _count -= fromBuffer;
            remaining -= fromBuffer;

            if (remaining > 0)
            {
                long available = _originalLength - _readPos;
                long advance = Math.Min(remaining, available);
                _readPos += advance;
                remaining -= advance;
                if (remaining > 0)
                    throw new EndOfStreamException("SkipSource requested more bytes than remain in the source file.");
            }
        }

        /// <summary>Writes brand-new bytes (not sourced from the original file) at the current
        /// write position — e.g. a rebuilt/edited segment. Safe regardless of length relative to
        /// whatever it's replacing; the lookahead buffer guarantees no unread original data is lost.</summary>
        public void WriteNew(byte[] data, int offset, int length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            if (length == 0) return;
            EnsureWriteSafe(length);
            _fs.Seek(_writePos, SeekOrigin.Begin);
            _fs.Write(data, offset, length);
            _writePos += length;
        }

        public void WriteNew(byte[] data) => WriteNew(data, 0, data.Length);

        /// <summary>Ensures at least <paramref name="n"/> bytes of original source content are
        /// sitting in the buffer, ready to be output or discarded — reading more from disk
        /// (growing the buffer if necessary) as needed, up to the original end of file.</summary>
        private void EnsureBuffered(int n) => PumpUntil(() => _count >= n);

        /// <summary>Ensures it is safe to physically write <paramref name="n"/> bytes at the current
        /// write position without clobbering not-yet-captured original content: guarantees the read
        /// cursor already covers file offset (writePosition + n), or the original end of file.</summary>
        private void EnsureWriteSafe(int n) => PumpUntil(() => _readPos >= _writePos + n);

        private void PumpUntil(Func<bool> satisfied)
        {
            while (!satisfied() && _readPos < _originalLength)
            {
                CompactIfNeeded();
                if (_head + _count >= _buf.Length) GrowBuffer();

                int spaceAtEnd = _buf.Length - (_head + _count);
                int want = (int)Math.Min(spaceAtEnd, _originalLength - _readPos);
                if (want <= 0) break;

                _fs.Seek(_readPos, SeekOrigin.Begin);
                int got = _fs.Read(_buf, _head + _count, want);
                if (got <= 0) break;
                _count += got;
                _readPos += got;
            }
        }

        /// <summary>Slides buffered-but-unconsumed bytes down to index 0 to reclaim space at the
        /// end of the buffer, without growing it.</summary>
        private void CompactIfNeeded()
        {
            if (_head == 0) return;
            if (_head + _count >= _buf.Length || _head > _buf.Length / 2)
            {
                Array.Copy(_buf, _head, _buf, 0, _count);
                _head = 0;
            }
        }

        private void GrowBuffer()
        {
            int newSize = Math.Max(_buf.Length * 2, _buf.Length + 4096);
            var next = new byte[newSize];
            Array.Copy(_buf, _head, next, 0, _count);
            _buf = next;
            _head = 0;
        }

        private void FlushBufferedBytes(int take)
        {
            _fs.Seek(_writePos, SeekOrigin.Begin);
            _fs.Write(_buf, _head, take);
            _head += take;
            _count -= take;
            _writePos += take;
        }

        /// <summary>Finalizes the rewrite. If the new content ended up shorter than the original
        /// file, truncates it to the final write position. By default, throws if there is still
        /// unconsumed original content (a likely bug — you probably meant to call
        /// CopyThroughRemaining() first); pass allowDroppingUnconsumedSource: true to intentionally
        /// discard whatever original content was never consumed.</summary>
        public void Finish(bool allowDroppingUnconsumedSource = false)
        {
            if (!allowDroppingUnconsumedSource && SourceBytesRemaining > 0)
                throw new InvalidOperationException(
                    $"{SourceBytesRemaining} byte(s) of the original file were never consumed " +
                    "(copied through or skipped). Call CopyThroughRemaining() before Finish(), " +
                    "or pass allowDroppingUnconsumedSource: true if that's intentional.");

            _fs.SetLength(_writePos);
            _fs.Flush();
        }

        public void Dispose() => _fs.Dispose();
    }
}