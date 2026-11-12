namespace Lit3Mail;

internal sealed class DraftSizeException(string message) : IOException(message);

internal static class ComposeAttachmentLimits
{
    private const int MaxFileBytes = 25 * 1024 * 1024;
    internal const int MaxTotalBytes = 50 * 1024 * 1024;
    internal const int MaxDraftMessageBytes = 80 * 1024 * 1024;
    internal const string Error = "Attachment limit exceeded (25 MiB per file, 50 MiB total).";

    internal static int RemainingFileBytes(int totalBytes) => Math.Min(MaxFileBytes, MaxTotalBytes - totalBytes);

    internal sealed class DecodeBuffer(int limit) : MemoryStream
    {
        private void CheckWrite(int count)
        {
            if (count > limit - Position)
                throw new DraftSizeException(Error);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckWrite(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckWrite(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            CheckWrite(1);
            base.WriteByte(value);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            CheckWrite(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            CheckWrite(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
