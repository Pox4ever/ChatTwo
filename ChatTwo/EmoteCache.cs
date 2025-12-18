using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Utility;
using Dalamud.Bindings.ImGui;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ChatTwo;

public static class EmoteCache
{
    private static readonly string[] NotWorking =
    [
        ":tf:", "(ditto)", "c!", "h!", "l!", "M&Mjc", "LUL3D", "p!",
        "POLICE2", "r!", "Pussy", "s!", "v!", "w!", "x0r6ztGiggle",
        "z!", "xar2EDM", "iron95Pls", "Clap2", "AlienPls3", "Life",
        "peepoPogClimbingTreeHard4House", "monkaGIGAftRobertDowneyJr",
        "DogLookingSussyAndCold", "DICKS"
    ];

    private static readonly HttpClient Client = new();

    private const string BetterTTV = "https://api.betterttv.net/3";
    private const string GlobalEmotes = $"{BetterTTV}/cached/emotes/global";
    private const string Top100Emotes = "{0}/emotes/shared/top?before={1}&limit=100";
    private const string EmotePath = "https://cdn.betterttv.net/emote/{0}/3x";
    
    // 7TV API endpoints
    private const string SevenTV = "https://7tv.io/v3";
    private const string SevenTVGlobalEmotes = $"{SevenTV}/emote-sets/global";
    private const string SevenTVUserByTwitchId = $"{SevenTV}/users/twitch/{{0}}"; // {0} = Twitch user ID
    private const string SevenTVEmotePath = "https://cdn.7tv.app/emote/{0}/4x.{1}";
    
    // Twitch Helix API endpoints
    private const string TwitchHelixUsers = "https://api.twitch.tv/helix/users?login={0}"; // {0} = username
    private const string TwitchHelixUsersSelf = "https://api.twitch.tv/helix/users"; // Get authenticated user info
    private const string TwitchClientId = "uo6dggojyb8d6soh92zknwmi5ej1q2"; // Alternative public Twitch client ID
    
    // Twitch OAuth endpoints
    private const string TwitchOAuthAuthorize = "https://id.twitch.tv/oauth2/authorize";
    private const string TwitchOAuthToken = "https://id.twitch.tv/oauth2/token";
    private static int TwitchOAuthPort = 8080; // Will be dynamically assigned
    


    private struct Top100
    {
        [JsonPropertyName("emote")]
        public Emote Emote { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; set; }
    }

    public struct Emote
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }
        [JsonPropertyName("code")]
        public string Code { get; set; }
        [JsonPropertyName("imageType")]
        public string ImageType { get; set; }
        public EmoteSource Source { get; set; }
    }

    public enum EmoteSource
    {
        BetterTTV,
        SevenTV
    }

    // 7TV API response structures
    private struct SevenTVEmoteSet
    {
        [JsonPropertyName("emotes")]
        public SevenTVEmote[] Emotes { get; set; }
    }

    private struct SevenTVEmote
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }
        [JsonPropertyName("name")]
        public string Name { get; set; }
        [JsonPropertyName("data")]
        public SevenTVEmoteData Data { get; set; }
    }

    private struct SevenTVEmoteData
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }
        [JsonPropertyName("animated")]
        public bool Animated { get; set; }
    }

    private struct SevenTVUserResponse
    {
        [JsonPropertyName("emote_set")]
        public SevenTVEmoteSet EmoteSet { get; set; }
    }

    // Twitch Helix API structures
    private struct TwitchUsersResponse
    {
        [JsonPropertyName("data")]
        public TwitchUser[] Data { get; set; }
    }

    private struct TwitchUser
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }
        [JsonPropertyName("login")]
        public string Login { get; set; }
        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; }
    }

    private struct TwitchOAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; }
        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
        [JsonPropertyName("token_type")]
        public string TokenType { get; set; }
    }



    public enum LoadingState
    {
        Unloaded,
        Loading,
        Done
    }

    // All of this data is uninitalized while State is not `LoadingState.Done`
    public static LoadingState State = LoadingState.Unloaded;

    private static readonly Dictionary<string, Emote> Cache = new();
    private static readonly Dictionary<string, EmoteBase> EmoteImages = new();

    public static string[] SortedCodeArray = [];

    public static async void LoadData()
    {
        if (State is not LoadingState.Unloaded)
            return;

        State = LoadingState.Loading;
        try
        {
            // Load BetterTTV emotes if enabled
            if (Plugin.Config.EnableBetterTTVEmotes)
            {
                await LoadBetterTTVEmotes();
            }

            // Load 7TV emotes if enabled
            if (Plugin.Config.EnableSevenTVEmotes)
            {
                await LoadSevenTVEmotes();
            }

            SortedCodeArray = Cache.Keys.Order().ToArray();
            State = LoadingState.Done;
            Plugin.Log.Info($"Loaded {Cache.Count} emotes from enabled sources");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Emote cache wasn't initialized");
        }
    }

    private static async Task LoadBetterTTVEmotes()
    {
        var betterTTVCount = 0;
        
        try
        {
            // Load global BetterTTV emotes
            try
            {
                var global = await Client.GetAsync(GlobalEmotes);
                if (global.IsSuccessStatusCode)
                {
                    var globalList = await global.Content.ReadAsStringAsync();
                    Plugin.Log.Info($"BetterTTV global response length: {globalList.Length}");
                    Plugin.Log.Info($"BetterTTV global response preview: {globalList.Substring(0, Math.Min(200, globalList.Length))}...");
                    
                    if (!string.IsNullOrWhiteSpace(globalList) && globalList.Trim().StartsWith("["))
                    {
                        var emotes = JsonSerializer.Deserialize<Emote[]>(globalList);
                        if (emotes != null)
                        {
                            Plugin.Log.Info($"Successfully parsed {emotes.Length} global BetterTTV emotes");
                            foreach (var emote in emotes)
                            {
                                if (!NotWorking.Contains(emote.Code))
                                {
                                    var emoteWithSource = emote;
                                    emoteWithSource.Source = EmoteSource.BetterTTV;
                                    if (Cache.TryAdd(emote.Code, emoteWithSource))
                                        betterTTVCount++;
                                }
                            }
                        }
                    }
                    else
                    {
                        Plugin.Log.Warning($"BetterTTV global response doesn't start with '[': {globalList.Substring(0, Math.Min(50, globalList.Length))}");
                    }
                }
                else
                {
                    Plugin.Log.Warning($"BetterTTV global emotes returned {global.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Failed to load global BetterTTV emotes, continuing with popular emotes");
            }

            // Load popular BetterTTV emotes
            try
            {
                var lastId = string.Empty;
                for (var i = 0; i < 3; i++) // Reduced to 3 to avoid rate limits and focus on debugging
                {
                    var url = Top100Emotes.Format(BetterTTV, lastId);
                    Plugin.Log.Info($"Requesting BetterTTV popular emotes page {i + 1}: {url}");
                    
                    var top = await Client.GetAsync(url);
                    if (top.IsSuccessStatusCode)
                    {
                        var topList = await top.Content.ReadAsStringAsync();
                        Plugin.Log.Info($"BetterTTV popular page {i + 1} response length: {topList.Length}");
                        Plugin.Log.Info($"BetterTTV popular page {i + 1} preview: {topList.Substring(0, Math.Min(200, topList.Length))}...");
                        
                        if (!string.IsNullOrWhiteSpace(topList) && topList.Trim().StartsWith("["))
                        {
                            var jsonList = JsonSerializer.Deserialize<List<Top100>>(topList);
                            if (jsonList != null && jsonList.Count > 0)
                            {
                                Plugin.Log.Info($"Successfully parsed {jsonList.Count} popular BetterTTV emotes from page {i + 1}");
                                foreach (var emote in jsonList)
                                {
                                    if (!NotWorking.Contains(emote.Emote.Code))
                                    {
                                        var emoteWithSource = emote.Emote;
                                        emoteWithSource.Source = EmoteSource.BetterTTV;
                                        if (Cache.TryAdd(emote.Emote.Code, emoteWithSource))
                                            betterTTVCount++;
                                    }
                                }
                                lastId = jsonList.Last().Id;
                            }
                            else
                            {
                                Plugin.Log.Info($"No more emotes found on page {i + 1}");
                                break; // No more emotes
                            }
                        }
                        else
                        {
                            Plugin.Log.Warning($"BetterTTV popular page {i + 1} response doesn't start with '[': {topList.Substring(0, Math.Min(50, topList.Length))}");
                            break;
                        }
                    }
                    else
                    {
                        Plugin.Log.Warning($"BetterTTV popular emotes page {i + 1} returned {top.StatusCode}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Failed to load popular BetterTTV emotes");
            }

            Plugin.Log.Info($"Loaded {betterTTVCount} BetterTTV emotes");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to load BetterTTV emotes");
        }
    }

    private static int FindAvailablePort()
    {
        // Try ports from 8080 to 8090
        for (int port = 8080; port <= 8090; port++)
        {
            try
            {
                using var listener = new System.Net.HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                listener.Stop();
                return port;
            }
            catch
            {
                // Port is in use, try next one
            }
        }
        
        // If all ports are busy, use a random high port
        using var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        socket.Start();
        var randomPort = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return randomPort;
    }

    public static void StartTwitchOAuth()
    {
        try
        {
            // Find an available port
            TwitchOAuthPort = FindAvailablePort();
            var redirectUri = $"http://localhost:{TwitchOAuthPort}/twitch-callback";
            
            // Generate a random state parameter for security
            var state = Guid.NewGuid().ToString("N")[..16];
            
            // Build the OAuth URL using implicit flow (token in URL fragment)
            var oauthUrl = $"{TwitchOAuthAuthorize}" +
                          $"?client_id={TwitchClientId}" +
                          $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                          $"&response_type=token" +
                          $"&scope=user:read:email" +
                          $"&state={state}";
            
            Plugin.Log.Info($"Starting Twitch OAuth flow on port {TwitchOAuthPort}: {oauthUrl}");
            
            // Start local HTTP server to handle callback
            _ = Task.Run(() => StartOAuthCallbackServer(state));
            
            // Open browser
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = oauthUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to start Twitch OAuth flow");
        }
    }

    private static async Task StartOAuthCallbackServer(string expectedState)
    {
        try
        {
            using var listener = new System.Net.HttpListener();
            listener.Prefixes.Add($"http://localhost:{TwitchOAuthPort}/");
            listener.Start();
            
            Plugin.Log.Info($"OAuth callback server listening on port {TwitchOAuthPort}");
            
            // Handle multiple requests (callback page + token POST)
            var startTime = DateTime.UtcNow;
            var timeout = TimeSpan.FromMinutes(5);
            
            while (DateTime.UtcNow - startTime < timeout)
            {
                var contextTask = listener.GetContextAsync();
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
                
                var completedTask = await Task.WhenAny(contextTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    continue; // Continue listening
                }
                
                var context = await contextTask;
                var request = context.Request;
                var response = context.Response;
                
                if (request.Url?.AbsolutePath == "/token" && request.HttpMethod == "POST")
                {
                    // Handle token POST request
                    var success = await HandleTokenPost(request);
                    
                    var result = success ? "success" : "failed";
                    var buffer = System.Text.Encoding.UTF8.GetBytes(result);
                    response.ContentLength64 = buffer.Length;
                    response.ContentType = "text/plain";
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    response.Close();
                    
                    if (success)
                    {
                        Plugin.Log.Info("OAuth flow completed successfully");
                        return; // Success, exit the server
                    }
                }
                else
                {
                    // Handle initial callback page
                    await SendOAuthCallbackPage(response, expectedState);
                }
            }
            
            Plugin.Log.Warning("Twitch OAuth timeout - user did not complete authorization");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "OAuth callback server error");
        }
    }

    private static async Task SendOAuthCallbackPage(System.Net.HttpListenerResponse response, string expectedState)
    {
        var html = $@"
<!DOCTYPE html>
<html>
<head>
    <title>ChatTwo - Twitch Authorization</title>
    <style>
        body {{ font-family: Arial, sans-serif; text-align: center; padding: 50px; }}
        .success {{ color: green; }}
        .error {{ color: red; }}
        .loading {{ color: blue; }}
    </style>
</head>
<body>
    <h1>ChatTwo - Twitch Authorization</h1>
    <p id=""status"" class=""loading"">🔄 Processing authorization...</p>
    
    <script>
        function processAuth() {{
            try {{
                // Extract token from URL fragment
                const fragment = window.location.hash.substring(1);
                const params = new URLSearchParams(fragment);
                
                const accessToken = params.get('access_token');
                const state = params.get('state');
                const error = params.get('error');
                
                if (error) {{
                    document.getElementById('status').innerHTML = '❌ Authorization failed: ' + error;
                    document.getElementById('status').className = 'error';
                    return;
                }}
                
                if (state !== '{expectedState}') {{
                    document.getElementById('status').innerHTML = '❌ Invalid state parameter';
                    document.getElementById('status').className = 'error';
                    return;
                }}
                
                if (!accessToken) {{
                    document.getElementById('status').innerHTML = '❌ No access token received';
                    document.getElementById('status').className = 'error';
                    return;
                }}
                
                // Send token to the plugin
                fetch('/token', {{
                    method: 'POST',
                    headers: {{ 'Content-Type': 'application/json' }},
                    body: JSON.stringify({{ access_token: accessToken }})
                }})
                .then(response => response.text())
                .then(result => {{
                    if (result === 'success') {{
                        document.getElementById('status').innerHTML = '✅ Successfully linked with Twitch! You can close this window and return to FFXIV.';
                        document.getElementById('status').className = 'success';
                    }} else {{
                        document.getElementById('status').innerHTML = '❌ Failed to save authorization: ' + result;
                        document.getElementById('status').className = 'error';
                    }}
                }})
                .catch(err => {{
                    document.getElementById('status').innerHTML = '❌ Error: ' + err.message;
                    document.getElementById('status').className = 'error';
                }});
            }} catch (err) {{
                document.getElementById('status').innerHTML = '❌ Error processing authorization: ' + err.message;
                document.getElementById('status').className = 'error';
            }}
        }}
        
        // Process auth when page loads
        window.onload = processAuth;
    </script>
</body>
</html>";
        
        var buffer = System.Text.Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = buffer.Length;
        response.ContentType = "text/html";
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        response.Close();
    }

    private static async Task<bool> HandleTokenPost(System.Net.HttpListenerRequest request)
    {
        try
        {
            using var reader = new System.IO.StreamReader(request.InputStream);
            var body = await reader.ReadToEndAsync();
            
            var tokenData = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
            if (tokenData == null || !tokenData.TryGetValue("access_token", out var accessToken))
            {
                Plugin.Log.Error("No access token in POST request");
                return false;
            }
            
            // Get user info with the new token
            var userInfo = await GetTwitchUserInfo(accessToken);
            if (userInfo == null)
            {
                Plugin.Log.Error("Failed to get user info with new token");
                return false;
            }
            
            // Save to configuration (implicit flow tokens typically expire in 4 hours)
            Plugin.Config.TwitchAccessToken = accessToken;
            Plugin.Config.TwitchUsername = userInfo.Value.Login;
            Plugin.Config.TwitchUserId = userInfo.Value.Id;
            Plugin.Config.TwitchTokenExpiry = DateTime.UtcNow.AddHours(4);
            
            Plugin.Log.Info($"Successfully authenticated as Twitch user: {userInfo.Value.Login} (ID: {userInfo.Value.Id})");
            
            // Reload emotes with new authentication
            State = LoadingState.Unloaded;
            LoadData();
            
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to handle token POST");
            return false;
        }
    }

    private static async Task<TwitchUser?> GetTwitchUserInfo(string accessToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, TwitchHelixUsersSelf);
            request.Headers.Add("Client-ID", TwitchClientId);
            request.Headers.Add("Authorization", $"Bearer {accessToken}");
            
            var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Plugin.Log.Error($"Failed to get user info: {response.StatusCode}");
                return null;
            }
            
            var content = await response.Content.ReadAsStringAsync();
            var usersResponse = JsonSerializer.Deserialize<TwitchUsersResponse>(content);
            
            return usersResponse.Data?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to get Twitch user info");
            return null;
        }
    }

    private static async Task<string[]> ConvertTwitchUsernamesToIds(string[] usernames)
    {
        var userIds = new List<string>();
        
        if (usernames.Length == 0)
            return userIds.ToArray();
            
        try
        {
            // Twitch API supports up to 100 usernames per request, but we'll batch smaller for reliability
            const int batchSize = 10;
            
            for (int i = 0; i < usernames.Length; i += batchSize)
            {
                var batch = usernames.Skip(i).Take(batchSize).ToArray();
                var loginParams = string.Join("&login=", batch);
                var url = $"https://api.twitch.tv/helix/users?login={loginParams}";
                
                Plugin.Log.Info($"Converting Twitch usernames to IDs: {string.Join(", ", batch)}");
                
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Client-ID", TwitchClientId);
                
                var response = await Client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var usersResponse = JsonSerializer.Deserialize<TwitchUsersResponse>(content);
                    
                    if (usersResponse.Data != null)
                    {
                        foreach (var user in usersResponse.Data)
                        {
                            userIds.Add(user.Id);
                            Plugin.Log.Info($"Converted '{user.Login}' -> ID: {user.Id}");
                        }
                    }
                }
                else
                {
                    Plugin.Log.Warning($"Failed to convert Twitch usernames: {response.StatusCode}");
                    Plugin.Log.Info("Falling back to manual user IDs if available");
                }
                
                // Rate limiting - wait between batches
                if (i + batchSize < usernames.Length)
                {
                    await Task.Delay(100);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to convert Twitch usernames to IDs");
        }
        
        return userIds.ToArray();
    }

    private static async Task LoadSevenTVEmotes()
    {
        var sevenTVCount = 0;
        
        try
        {
            // Load global 7TV emotes
            try
            {
                var global = await Client.GetAsync(SevenTVGlobalEmotes);
                if (global.IsSuccessStatusCode)
                {
                    var globalResponse = await global.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(globalResponse) && globalResponse.Trim().StartsWith("{"))
                    {
                        var globalSet = JsonSerializer.Deserialize<SevenTVEmoteSet>(globalResponse);
                        if (globalSet.Emotes != null)
                        {
                            foreach (var sevenTVEmote in globalSet.Emotes)
                            {
                                if (!NotWorking.Contains(sevenTVEmote.Name))
                                {
                                    // Use the emote ID from data.id, fallback to top-level id if needed
                                    var emoteId = !string.IsNullOrEmpty(sevenTVEmote.Data.Id) ? sevenTVEmote.Data.Id : sevenTVEmote.Id;
                                    
                                    var emote = new Emote
                                    {
                                        Id = emoteId,
                                        Code = sevenTVEmote.Name,
                                        ImageType = sevenTVEmote.Data.Animated ? "gif" : "webp",
                                        Source = EmoteSource.SevenTV
                                    };
                                    if (Cache.TryAdd(sevenTVEmote.Name, emote))
                                        sevenTVCount++;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Failed to load global 7TV emotes, continuing with channel emotes");
            }

            // Load channel-specific 7TV emotes using Twitch user IDs
            var allUserIds = new List<string>();
            
            // Add manual user IDs
            if (!string.IsNullOrWhiteSpace(Plugin.Config.TwitchUserIds))
            {
                var manualIds = Plugin.Config.TwitchUserIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(id => id.Trim())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToArray();
                
                allUserIds.AddRange(manualIds);
                Plugin.Log.Info($"Added {manualIds.Length} Twitch user ID(s): {string.Join(", ", manualIds)}");
            }
            
            // If no user IDs configured, just use global 7TV emotes (no fallback needed)
            if (allUserIds.Count == 0)
            {
                Plugin.Log.Info("No Twitch user IDs configured - only global 7TV emotes will be loaded");
            }
            
            // Remove duplicates while preserving order
            var userIds = allUserIds.Distinct().ToArray();
            
            if (userIds.Length > 0)
            {
                Plugin.Log.Info($"Loading 7TV emotes from {userIds.Length} streamer(s)");
                
                for (int userIndex = 0; userIndex < userIds.Length; userIndex++)
            {
                var twitchUserId = userIds[userIndex];
                try
                {
                    var url = SevenTVUserByTwitchId.Format(twitchUserId);
                    Plugin.Log.Info($"Loading 7TV emotes for Twitch user ID: {twitchUserId} (priority {userIndex + 1}/{userIds.Length})");
                    Plugin.Log.Info($"7TV API URL: {url}");
                    
                    var response = await Client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        Plugin.Log.Info($"7TV API response length: {content.Length}");
                        Plugin.Log.Info($"7TV API response preview: {content.Substring(0, Math.Min(500, content.Length))}...");
                        
                        if (!string.IsNullOrWhiteSpace(content) && content.Trim().StartsWith("{"))
                        {
                            try
                            {
                                var userResponse = JsonSerializer.Deserialize<SevenTVUserResponse>(content);
                                Plugin.Log.Info($"Parsed 7TV response - EmoteSet exists: {userResponse.EmoteSet.Emotes != null}");
                                
                                if (userResponse.EmoteSet.Emotes != null && userResponse.EmoteSet.Emotes.Length > 0)
                                {
                                    Plugin.Log.Info($"Found {userResponse.EmoteSet.Emotes.Length} emotes for Twitch user ID '{twitchUserId}'");
                                    
                                    // Debug: Show first few emotes
                                    for (int i = 0; i < Math.Min(3, userResponse.EmoteSet.Emotes.Length); i++)
                                    {
                                        var debugEmote = userResponse.EmoteSet.Emotes[i];
                                        var debugEmoteId = !string.IsNullOrEmpty(debugEmote.Data.Id) ? debugEmote.Data.Id : debugEmote.Id;
                                        Plugin.Log.Info($"Sample emote {i + 1}: '{debugEmote.Name}' -> ID: {debugEmoteId}, Animated: {debugEmote.Data.Animated}");
                                    }
                                    
                                    foreach (var sevenTVEmote in userResponse.EmoteSet.Emotes)
                                    {
                                        if (!NotWorking.Contains(sevenTVEmote.Name))
                                        {
                                            // Use the emote ID from data.id, fallback to top-level id if needed
                                            var emoteId = !string.IsNullOrEmpty(sevenTVEmote.Data.Id) ? sevenTVEmote.Data.Id : sevenTVEmote.Id;
                                            
                                            var emote = new Emote
                                            {
                                                Id = emoteId,
                                                Code = sevenTVEmote.Name,
                                                ImageType = sevenTVEmote.Data.Animated ? "gif" : "webp",
                                                Source = EmoteSource.SevenTV
                                            };
                                            if (Cache.TryAdd(sevenTVEmote.Name, emote))
                                            {
                                                sevenTVCount++;
                                            }
                                            else
                                            {
                                                Plugin.Log.Debug($"Emote '{sevenTVEmote.Name}' already exists, skipping (first occurrence takes precedence)");
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    Plugin.Log.Warning($"7TV user has no emotes or emote_set is empty");
                                }
                            }
                            catch (Exception parseEx)
                            {
                                Plugin.Log.Error(parseEx, $"Failed to parse 7TV response for Twitch user ID '{twitchUserId}'");
                            }
                        }
                        else
                        {
                            Plugin.Log.Warning($"7TV API returned invalid JSON: {content.Substring(0, Math.Min(100, content.Length))}");
                        }
                    }
                    else
                    {
                        Plugin.Log.Warning($"7TV API returned {response.StatusCode} for Twitch user ID '{twitchUserId}'");
                        Plugin.Log.Info("Make sure your Twitch user ID is correct. You can find it at: https://www.streamweasels.com/tools/convert-twitch-username-to-user-id/");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, $"Failed to load 7TV emotes for Twitch user ID '{twitchUserId}'");
                }
                }
            }
            else
            {
                Plugin.Log.Info("No Twitch user ID configured for 7TV emotes. Please add your Twitch user ID in settings.");
            }

            Plugin.Log.Info($"Loaded {sevenTVCount} 7TV emotes");
            if (sevenTVCount > 0)
            {
                var sevenTVEmoteNames = Cache.Where(e => e.Value.Source == EmoteSource.SevenTV).Take(10).Select(e => e.Key);
                Plugin.Log.Info($"Sample 7TV emotes loaded: {string.Join(", ", sevenTVEmoteNames)}");
                
                // Debug: Show first few 7TV emotes with their details
                var sevenTVEmotes = Cache.Where(e => e.Value.Source == EmoteSource.SevenTV).Take(3);
                foreach (var kvp in sevenTVEmotes)
                {
                    Plugin.Log.Info($"7TV emote: '{kvp.Key}' -> ID: {kvp.Value.Id}, Type: {kvp.Value.ImageType}");
                }
            }
            else
            {
                Plugin.Log.Warning("No 7TV emotes were loaded successfully");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to load 7TV emotes");
        }
    }

    public static void Dispose()
    {
        foreach (var emote in EmoteImages.Values)
            emote.InnerDispose();
    }

    internal static bool Exists(string code)
    {
        return State is LoadingState.Done && SortedCodeArray.Contains(code);
    }

    internal static EmoteBase? GetEmote(string code)
    {
        if (State is not LoadingState.Done)
            return null;

        if (!Cache.TryGetValue(code, out var emoteDetail))
            return null;

        if (EmoteImages.TryGetValue(emoteDetail.Id, out var emote))
            return emote;

        try
        {
            if (emoteDetail.ImageType == "gif")
            {
                var animatedEmote = new ImGuiGif().Prepare(emoteDetail);
                EmoteImages.Add(emoteDetail.Id, animatedEmote);
                return animatedEmote;
            }

            var staticEmote = new ImGuiEmote().Prepare(emoteDetail);
            EmoteImages.Add(emoteDetail.Id, staticEmote);

            return staticEmote;
        }
        catch
        {
            Plugin.Log.Error("Failed to convert");
            return null;
        }
    }

    public abstract class EmoteBase
    {
        public bool Failed;
        public bool IsLoaded;

        public byte[] RawData = [];

        protected IDalamudTextureWrap? Texture;

        public virtual void Draw(Vector2 size)
        {
            ImGui.Image(Texture!.Handle, size);
        }

        internal async Task<byte[]> LoadAsync(Emote emote)
        {
            var dir = Path.Join(Plugin.Interface.ConfigDirectory.FullName, "EmoteCacheV2");
            Directory.CreateDirectory(dir);

            var filePath = Path.Join(dir, $"{emote.Source}_{emote.Id}.{emote.ImageType}");
            if (File.Exists(filePath))
            {
                RawData = await File.ReadAllBytesAsync(filePath);
            }
            else
            {
                var emotePath = emote.Source switch
                {
                    EmoteSource.BetterTTV => EmotePath.Format(emote.Id),
                    EmoteSource.SevenTV => SevenTVEmotePath.Format(emote.Id, emote.ImageType),
                    _ => throw new ArgumentException($"Unknown emote source: {emote.Source}")
                };

                var content = await new HttpClient().GetAsync(emotePath);
                RawData = await content.Content.ReadAsByteArrayAsync();

                await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                stream.Write(RawData, 0, RawData.Length);
            }

            return RawData;
        }

        public abstract void InnerDispose();
    }

    public sealed class ImGuiEmote : EmoteBase
    {
        public ImGuiEmote Prepare(Emote emote)
        {
            Task.Run(() => Load(emote));
            return this;
        }

        private async void Load(Emote emote)
        {
            try
            {
                var image = await LoadAsync(emote);
                if (image.Length <= 0)
                    return;

                Texture = await Plugin.TextureProvider.CreateFromImageAsync(image);
                IsLoaded = true;
            }
            catch (Exception ex)
            {
                Failed = true;
                Plugin.Log.Error(ex, $"Unable to load {emote.Code} with id {emote.Id}");
            }
        }

        public override void InnerDispose()
        {
            Texture?.Dispose();
        }
    }

    public sealed class ImGuiGif : EmoteBase
    {
        private List<(IDalamudTextureWrap Texture, float Delay)> Frames = [];
        private float FrameTimer;
        private int CurrentFrame;
        private ulong GlobalFrameCount;

        public override void Draw(Vector2 size)
        {
            if (Frames.Count == 0)
                return;

            if (CurrentFrame >= Frames.Count)
            {
                CurrentFrame = 0;
                FrameTimer = -1f;
            }

            var frame = Frames[CurrentFrame];
            if (FrameTimer <= 0.0f)
                FrameTimer = frame.Delay;

            ImGui.Image(frame.Texture.Handle, size);

            if (GlobalFrameCount == Plugin.Interface.UiBuilder.FrameCount)
                return;

            GlobalFrameCount = Plugin.Interface.UiBuilder.FrameCount;

            FrameTimer -= ImGui.GetIO().DeltaTime;
            if (FrameTimer <= 0f)
                CurrentFrame++;
        }

        public override void InnerDispose()
        {
            Frames.ForEach(f => f.Texture.Dispose());
            Frames.Clear();
        }

        public ImGuiGif Prepare(Emote emote)
        {
            Task.Run(() => Load(emote));
            return this;
        }

        private async void Load(Emote emote)
        {
            try
            {
                var image = await LoadAsync(emote);
                if (image.Length <= 0)
                    return;

                using var ms = new MemoryStream(image);
                using var img = Image.Load<Rgba32>(ms);
                if (img.Frames.Count == 0)
                    return;

                var frames = new List<(IDalamudTextureWrap Tex, float Delay)>();
                foreach (var frame in img.Frames)
                {
                    var delay = frame.Metadata.GetGifMetadata().FrameDelay / 100f;

                    // Follows the same pattern as browsers, anything under 0.02s delay will be rounded up to 0.1s
                    if (delay < 0.02f)
                        delay = 0.1f;

                    var buffer = new byte[4 * frame.Width * frame.Height];
                    frame.CopyPixelDataTo(buffer);
                    var tex = await Plugin.TextureProvider.CreateFromRawAsync(RawImageSpecification.Rgba32(frame.Width, frame.Height), buffer);
                    frames.Add((tex, delay));
                }

                Frames = frames;
                IsLoaded = true;
            }
            catch (Exception ex)
            {
                Failed = true;
                Plugin.Log.Error(ex, $"Unable to load {emote.Code} with id {emote.Id}");
            }
        }
    }
}