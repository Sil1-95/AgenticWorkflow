namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// A request a caller wrote to a file because it cannot feed standard input (a Herdr mod starts <c>aw</c> with an argument list and nothing else). The file holds what a person typed (an
/// objective, feedback, an answer, a project's configuration), so it is read once, at most <c>maxBytes</c>, and deleted whatever the outcome: it exists only to carry the text here.
/// </summary>
internal static class RequestFile
{
    /// <summary>The bytes of the file, or why they could not be read. The file is deleted in every case.</summary>
    public static async Task<(byte[]? Bytes, string? Problem)> ConsumeAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);

            if (!info.Exists)
                return (null, "The request file does not exist.");

            if (info.Length > maxBytes)
                return (null, $"A request is at most {maxBytes} bytes.");

            return (await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, $"The request file could not be read: {e.Message}");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a file that cannot be deleted is left for its owner; it is never read again
            }
        }
    }
}
