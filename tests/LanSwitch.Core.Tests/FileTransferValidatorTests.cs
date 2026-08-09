using System.Security.Cryptography;
using LanSwitch.Core.Files;
using LanSwitch.Core.Models;

namespace LanSwitch.Core.Tests;

public sealed class FileTransferValidatorTests
{
    [Fact]
    public void EveryChunk_IncludingShortFinalChunk_IsValidated()
    {
        byte[] file = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        var offer = FileOffer.Create("sample.bin", file.Length, Hash(file), chunkSize: 4);

        for (var index = 0L; index < offer.ChunkCount; index++)
        {
            var offset = index * offer.ChunkSize;
            var length = FileTransferValidator.GetExpectedChunkLength(offer, index);
            var chunk = file.AsSpan((int)offset, length);

            var result = FileTransferValidator.ValidateChunk(offer, index, offset, chunk, Hash(chunk));

            Assert.True(result.IsValid, result.Message);
        }

        Assert.Equal(3L, offer.ChunkCount);
        Assert.Equal(2, FileTransferValidator.GetExpectedChunkLength(offer, 2));
    }

    [Fact]
    public void WrongChunkHash_IsRejected()
    {
        byte[] file = [1, 2, 3, 4];
        var offer = FileOffer.Create("sample.bin", file.Length, Hash(file), chunkSize: 4);

        var result = FileTransferValidator.ValidateChunk(
            offer,
            chunkIndex: 0,
            offset: 0,
            file,
            new string('0', 64));

        Assert.Equal(FileValidationError.ChunkHashMismatch, result.Error);
    }

    [Fact]
    public async Task CompleteFile_VerifiesLengthAndSha256()
    {
        byte[] file = [10, 20, 30, 40, 50];
        var offer = FileOffer.Create("sample.bin", file.Length, Hash(file), chunkSize: 2);
        await using var stream = new MemoryStream(file);

        var result = await FileTransferValidator.VerifyFileAsync(stream, offer);

        Assert.True(result.IsValid, result.Message);
    }

    [Fact]
    public async Task ModifiedCompleteFile_IsRejected()
    {
        byte[] original = [10, 20, 30];
        byte[] modified = [10, 20, 31];
        var offer = FileOffer.Create("sample.bin", original.Length, Hash(original), chunkSize: 2);
        await using var stream = new MemoryStream(modified);

        var result = await FileTransferValidator.VerifyFileAsync(stream, offer);

        Assert.Equal(FileValidationError.FileHashMismatch, result.Error);
    }

    private static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content));
}
