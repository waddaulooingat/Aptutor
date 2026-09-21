using Avalonia.Media.Imaging;

namespace ApTutor.Client.Services;

/// The PSAT Tutor image stopgap's display half (see that handoff's Part E) — downloads a plain
/// image URL an SME attached to a practice item (a diagram authored/hosted outside this codebase)
/// and decodes it into something Avalonia can show. Deliberately just an HTTP GET + Bitmap decode,
/// no caching, no retry: this is a scoped detour's stopgap, not shipping infrastructure. A failed
/// load (bad URL, offline, unsupported format) is logged and treated as "no image" rather than
/// crashing the practice panel around it — the question's text/choices still work either way.
public static class RemoteImageLoader
{
    private static readonly HttpClient Http = new();

    public static async Task<Bitmap?> TryLoadAsync(string url, CancellationToken ct = default)
    {
        try
        {
            await using var stream = await Http.GetStreamAsync(url, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            return new Bitmap(buffer);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RemoteImageLoader] Could not load diagram from '{url}': {ex.Message}");
            return null;
        }
    }
}
