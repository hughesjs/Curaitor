using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Web;
using LangChain.DocumentLoaders;
using SpotifyAPI.Web;

namespace Curaitor.ProofOfConcept;

public class SpotifyTracksLoader : IDocumentLoader
{
    public async Task<IReadOnlyCollection<Document>> LoadAsync(DataSource dataSource, DocumentLoaderSettings? settings = null, CancellationToken cancellationToken = new CancellationToken())
    {
        SpotifyClient spotify = await AuthenticateWithSpotify();
        List<SavedTrack> tracks = await FetchSpotifyTracks(spotify);
        ImmutableList<Document> docs =  tracks.Select(t => new Document($"{t.Track.Name} - {t.Track.Album.Name} - {t.Track.Artists.First().Name}")).ToImmutableList();

        return docs;
    }

    // NOTE: This doesn't even pretend to be robust or secure, it's not the point of this PoC
    async Task<SpotifyClient> AuthenticateWithSpotify()
    {
        string? clientId = Environment.GetEnvironmentVariable("SPOTIFY_CLIENT_ID");
        string? clientSecret = Environment.GetEnvironmentVariable("SPOTIFY_CLIENT_SECRET");

        if (clientId is null || clientSecret is null)
        {
            Console.WriteLine("Please set the SPOTIFY_CLIENT_[ID|SECRET] env vars");
            throw new();
        }

        Console.WriteLine("Authenticating with Spotify");

        if (File.Exists("access-token"))
        {
            Console.WriteLine("Attempting to use saved token");
            string accessToken = (await File.ReadAllTextAsync("access-token")).Trim();
            try
            {
                // TODO - Test if this actually logs in and checks the token
                return new(accessToken);
            }
            catch
            {
                Console.WriteLine("Attempting to refresh expired token");
                string refreshToken = (await File.ReadAllTextAsync("refresh-token")).Trim();
                AuthorizationCodeRefreshRequest req = new(clientId, clientSecret, refreshToken);
                AuthorizationCodeRefreshResponse refreshedToken = await new OAuthClient().RequestToken(req);
                Console.WriteLine("Storing token and refresh token unsecurely locally");

                await File.WriteAllTextAsync("access-token", refreshedToken.AccessToken);
                await File.WriteAllTextAsync("refresh-token", refreshedToken.RefreshToken);

                Console.WriteLine("Authentication Complete");

                return new(refreshedToken.AccessToken);
            }
        }

        LoginRequest loginRequest = new(new("http://127.0.0.1:5543/callback"), clientId, LoginRequest.ResponseType.Code)
        {
            Scope = [Scopes.PlaylistReadPrivate, Scopes.PlaylistReadCollaborative, Scopes.UserLibraryRead]
        };

        Uri uri = loginRequest.ToUri();

        Console.WriteLine(uri);

        TcpListener listener = new(IPAddress.Loopback, 5543);
        listener.Start();
        using TcpClient client = await listener.AcceptTcpClientAsync();
        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream);
        await using StreamWriter writer = new(stream) { NewLine = "\r\n", AutoFlush = true };

        string? requestLine = await reader.ReadLineAsync();
        if (requestLine is null)
        {
            throw new("Invalid auth callback received");
        }

        string uriPart = requestLine.Split(' ')[1];
        string query = uriPart[uriPart.IndexOf("?code=", StringComparison.Ordinal)..];
        NameValueCollection qs = HttpUtility.ParseQueryString(query);
        string? code = qs["code"];

        await writer.WriteLineAsync("HTTP/1.1 200 OK");
        await writer.WriteLineAsync("Content-Type: text/html");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("<html><body><h1>Authorisation complete</h1>" +
                                    "<p>You can close this window.</p></body></html>");

        listener.Stop();

        if (code is null)
        {
            throw new("Invalid auth callback");
        }

        AuthorizationCodeTokenRequest authReq = new(clientId, clientSecret, code, new("http://127.0.0.1:5543/callback"));
        AuthorizationCodeTokenResponse response = await new OAuthClient().RequestToken(authReq);

        Console.WriteLine("Storing token and refresh token unsecurely locally");

        await File.WriteAllTextAsync("access-token", response.AccessToken);
        await File.WriteAllTextAsync("refresh-token", response.RefreshToken);

        Console.WriteLine("Authentication Complete");

        return new(response.AccessToken);
    }

    async Task<List<SavedTrack>> FetchSpotifyTracks(SpotifyClient spotify)
    {
        Console.WriteLine("Pulling your saved songs...");

        if (File.Exists("saved-tracks"))
        {
            Console.WriteLine("Reading in from saved file...");
            string tracksJson = (await File.ReadAllTextAsync("saved-tracks")).Trim();
            return JsonSerializer.Deserialize<List<SavedTrack>>(tracksJson)!;
        }

        const int limit = 50;

        Paging<SavedTrack> tracks = await spotify.Library.GetTracks(new LibraryTracksRequest() { Limit = limit });

        if (tracks.Total is null or 0)
        {
            Console.WriteLine("Failed to fetch tracks for user");
            throw new();
        }

        // Yes, I know this is memory inefficient... ¯\_(ツ)_/¯

        Console.WriteLine($"Fetching info for your {tracks.Total.Value} saved tracks...");

        List<SavedTrack> allTracks = new(tracks.Total.Value);

        while (tracks.Items is not null && tracks.Items.Count > 0)
        {
            allTracks.AddRange(tracks.Items);
            Console.WriteLine($"{Math.Clamp(tracks.Offset!.Value + tracks.Limit!.Value, 0, tracks.Total!.Value)}/{tracks.Total}");
            tracks = await spotify.Library.GetTracks(new LibraryTracksRequest { Offset = tracks.Offset + limit, Limit = limit });
        }

        string json = JsonSerializer.Serialize(allTracks);

        Console.WriteLine("Saving tracks to disk as json...");
        await File.WriteAllTextAsync("saved-tracks", json);

        return allTracks;
    }
}