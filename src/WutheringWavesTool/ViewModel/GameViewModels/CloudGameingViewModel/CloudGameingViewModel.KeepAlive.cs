namespace Haiyu.ViewModel.GameViewModels;

partial class CloudGameingViewModel
{
    private CancellationTokenSource? _cloudSessionKeepAliveCts;

    private void StartCloudSessionKeepAlive()
    {
        StopCloudSessionKeepAlive();
        if (string.IsNullOrWhiteSpace(Option.CloudApiToken))
            return;

        _cloudSessionKeepAliveCts = new CancellationTokenSource();
        var token = _cloudSessionKeepAliveCts.Token;
        // Observe exceptions so Cancel() does not surface as unhandled OCE.
        _ = Task.Run(async () =>
        {
            try
            {
                await RunCloudSessionKeepAliveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Logger.WriteInfo("[CloudGame][KeepAlive] stopped (canceled)");
            }
            catch (Exception ex)
            {
                Logger.WriteWarning($"[CloudGame][KeepAlive] exited: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private void StopCloudSessionKeepAlive()
    {
        var cts = Interlocked.Exchange(ref _cloudSessionKeepAliveCts, null);
        if (cts is null)
            return;
        try
        {
            cts.Cancel();
        }
        catch
        {
        }
        cts.Dispose();
        Logger.WriteInfo("[CloudGame][KeepAlive] cancel requested");
    }

    private async Task RunCloudSessionKeepAliveAsync(CancellationToken token)
    {
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            UseCookies = false,
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://cloud-game-sh.aki-game.com/"),
            // Prefer token cancel over HttpClient.Timeout so dispose is clean.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("x-token", Option.CloudApiToken);
        if (Option.ClientPlatform == "Android")
        {
            // 安卓主业务 API 实测只带 x-token（OkHttp UA），无 x-os / Kr-Ver / Referer
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                Waves.Core.Services.CloudGameServices.AndroidCloudGameMethod.UserAgent
            );
        }
        else
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("x-os", "web");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Kr-Ver", "1.9.0");
            client.DefaultRequestHeaders.Referrer = new Uri("https://mc.kurogames.com/cloud/index.html");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Code/1.120.0 Chrome/142.0.7444.265 Electron/39.8.8 Safari/537.36"
            );
        }
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");

        var consecutiveFailures = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
                using var response = await client.GetAsync("GamePlay/SessionInfo", timeoutCts.Token);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"SessionInfo HTTP {(int)response.StatusCode}");

                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Logger.WriteInfo("[CloudGame][KeepAlive] GetAsync canceled by stop");
                break;
            }
            catch (OperationCanceledException)
            {
                // Per-request timeout (not full stop).
                consecutiveFailures++;
                Logger.WriteWarning($"[CloudGame][KeepAlive] SessionInfo timeout/cancel failures={consecutiveFailures}");
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                Logger.WriteWarning($"[CloudGame][KeepAlive] SessionInfo failed: {ex.GetType().Name}: {ex.Message}");
                if (consecutiveFailures >= 2 && WebView2?.CoreWebView2 is not null)
                {
                    Window.DispatcherQueue.TryEnqueue(async () =>
                    {
                        try
                        {
                            await WebView2.CoreWebView2.ExecuteScriptAsync(
                                "window.__KURO_STREAM_CONTROL__?.reconnect?.('session-heartbeat');"
                            );
                        }
                        catch (Exception scriptEx)
                        {
                            Logger.WriteWarning(
                                $"[CloudGame][KeepAlive] reconnect script failed: {scriptEx.GetType().Name}: {scriptEx.Message}"
                            );
                        }
                    });
                }
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(token))
                    break;
            }
            catch (OperationCanceledException)
            {
                Logger.WriteInfo("[CloudGame][KeepAlive] timer canceled");
                break;
            }
        }
    }
}
