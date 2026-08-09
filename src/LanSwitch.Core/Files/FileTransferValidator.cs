using System.Buffers;
using System.Security.Cryptography;
using LanSwitch.Core.Internal;
using LanSwitch.Core.Models;

namespace LanSwitch.Core.Files;

public enum FileValidationError
{
    None,
    EmptyTransferId,
    InvalidFileName,
    InvalidLength,
    InvalidSha256,
    InvalidChunkSize,
    InvalidChunkCount,
    InvalidChunkIndex,
    InvalidChunkOffset,
    InvalidChunkLength,
    ChunkHashMismatch,
    FileLengthMismatch,
    FileHashMismatch
}

public readonly record struct FileValidationResult(
    bool IsValid,
    FileValidationError Error,
    string? Message)
{
    public static FileValidationResult Success { get; } = new(true, FileValidationError.None, null);

    public static FileValidationResult Failure(FileValidationError error, string message)
    {
        return new FileValidationResult(false, error, message);
    }
}

public static class FileTransferValidator
{
    public const int MaximumChunkSize = 4 * 1024 * 1024;

    public static FileValidationResult ValidateOffer(FileOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);

        if (offer.TransferId == Guid.Empty)
        {
            return FileValidationResult.Failure(FileValidationError.EmptyTransferId, "TransferId must not be empty.");
        }

        if (!IsSafeFileName(offer.FileName))
        {
            return FileValidationResult.Failure(
                FileValidationError.InvalidFileName,
                "FileName must be a single safe file name without path components.");
        }

        if (offer.Length < 0)
        {
            return FileValidationResult.Failure(FileValidationError.InvalidLength, "Length must not be negative.");
        }

        if (!HexUtility.IsSha256(offer.Sha256))
        {
            return FileValidationResult.Failure(
                FileValidationError.InvalidSha256,
                "Sha256 must contain exactly 64 hexadecimal characters.");
        }

        if (offer.ChunkSize is <= 0 or > MaximumChunkSize)
        {
            return FileValidationResult.Failure(
                FileValidationError.InvalidChunkSize,
                $"ChunkSize must be between 1 and {MaximumChunkSize} bytes.");
        }

        var expectedChunkCount = CalculateChunkCount(offer.Length, offer.ChunkSize);
        if (offer.ChunkCount != expectedChunkCount)
        {
            return FileValidationResult.Failure(
                FileValidationError.InvalidChunkCount,
                $"ChunkCount must be {expectedChunkCount} for the offered length and chunk size.");
        }

        return FileValidationResult.Success;
    }

    public static FileValidationResult ValidateChunk(
        FileOffer offer,
        long chunkIndex,
        long offset,
        ReadOnlySpan<byte> content,
        string expectedChunkSha256)
    {
        var offerResult = ValidateOffer(offer);
        if (!offerResult.IsValid)
        {
            return offerResult;
        }

        if (chunkIndex < 0 || chunkIndex >= offer.ChunkCount)
        {
            return FileValidationResult.Failure(
                FileValidationError.InvalidChunkIndex,
                $"Chunk index {chunkIndex} is outside the offered range.");
        }

        var expectedOffset = checked(chunkIndex * (long)offer.ChunkSize);
        if (offset != expectedOffset)
        {
            return FileValidationResult.Failure(
                FileValidationError.InvalidChunkOffset,
                $"Chunk offset {offset} does not match expected offset {expectedOffset}.");
        }

        var expectedLength = GetExpectedChunkLength(offer, chunkIndex);
        if (content.Length != expectedLength)
        {
            return FileValidationResult.Failure(
                FileValidationError.InvalidChunkLength,
                $"Chunk length {content.Length} does not match expected length {expectedLength}.");
        }

        if (!HexUtility.IsSha256(expectedChunkSha256))
        {
            return FileValidationResult.Failure(
                FileValidationError.InvalidSha256,
                "The chunk SHA-256 value is malformed.");
        }

        var actualHash = SHA256.HashData(content);
        var expectedHash = Convert.FromHexString(expectedChunkSha256);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            return FileValidationResult.Failure(
                FileValidationError.ChunkHashMismatch,
                $"Chunk {chunkIndex} did not match its SHA-256 digest.");
        }

        return FileValidationResult.Success;
    }

    public static async ValueTask<FileValidationResult> VerifyFileAsync(
        Stream content,
        FileOffer offer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(content));
        }

        var offerResult = ValidateOffer(offer);
        if (!offerResult.IsValid)
        {
            return offerResult;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytesRead = 0;
            while (true)
            {
                var read = await content
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                bytesRead = checked(bytesRead + read);
                if (bytesRead > offer.Length)
                {
                    return FileValidationResult.Failure(
                        FileValidationError.FileLengthMismatch,
                        $"Received more than the offered {offer.Length} bytes.");
                }

                hash.AppendData(buffer.AsSpan(0, read));
            }

            if (bytesRead != offer.Length)
            {
                return FileValidationResult.Failure(
                    FileValidationError.FileLengthMismatch,
                    $"Received {bytesRead} bytes, but the offer declared {offer.Length} bytes.");
            }

            var actualHash = hash.GetHashAndReset();
            var expectedHash = Convert.FromHexString(offer.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                return FileValidationResult.Failure(
                    FileValidationError.FileHashMismatch,
                    "The completed file did not match the offered SHA-256 digest.");
            }

            return FileValidationResult.Success;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public static long CalculateChunkCount(long length, int chunkSize)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (chunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        }

        return length == 0 ? 0 : ((length - 1) / chunkSize) + 1;
    }

    public static int GetExpectedChunkLength(FileOffer offer, long chunkIndex)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (chunkIndex < 0 || chunkIndex >= offer.ChunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        }

        var offset = checked(chunkIndex * (long)offer.ChunkSize);
        return (int)Math.Min(offer.ChunkSize, offer.Length - offset);
    }

    private static bool IsSafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
        {
            return false;
        }

        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            return false;
        }

        return fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }
}
