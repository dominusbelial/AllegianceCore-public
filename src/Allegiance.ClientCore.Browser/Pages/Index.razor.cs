using Allegiance.Core;
using Allegiance.ClientCore.Browser.Assets;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Allegiance.ClientCore.Browser.Pages;

public partial class Index
{
    private PreviewGame? _game;
    private DotNetObjectReference<Index>? _reference;
    private string? _error;
    private string _status = "Preparing artwork…";
    private int _progress;
    private bool _loading;
    private bool _attempted;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _error = null;
        _progress = 0;
        StateHasChanged();
        try
        {
            // Retry bypasses the HTTP cache, including an immutable but corrupt response.
            var loader = new PreviewAssetLoader(Http, _attempted
                ? request => request.SetBrowserRequestCache(BrowserRequestCache.Reload) : null);
            _attempted = true;
            var assets = await loader.LoadAsync((percent, status) =>
            {
                _progress = percent;
                _status = status;
                StateHasChanged();
            });
            if (assets is null) { _error = loader.Error; return; }
            _game?.Dispose();
            _game = new PreviewGame(MdlFile.Parse(assets["fig05.mdl"]), MdlTextureDecoder.Decode(assets["fig05bmp.mdl"]));
            _reference ??= DotNetObjectReference.Create(this);
            await JsRuntime.InvokeVoidAsync("preview.startFrames", _reference);
        }
        catch (Exception)
        {
            _game?.Dispose();
            _game = null;
            _error = "The artwork or WebGL renderer could not start. Retry to try again.";
        }
        finally { _loading = false; StateHasChanged(); }
    }

    private void Rotate(float amount) => _game?.Rotate(amount);
    private void Zoom(float amount) => _game?.Zoom(amount);

    [JSInvokable]
    public bool TickDotNet()
    {
        try
        {
            if (_game is null) return false;
            if (!_game.Started) { _game.Run(); _game.Started = true; }
            _game.Tick();
            return true;
        }
        catch (Exception)
        {
            _error = "WebGL rendering stopped. Retry to restart the preview.";
            _game?.Dispose();
            _game = null;
            StateHasChanged();
            return false;
        }
    }

    public void Dispose() { _game?.Dispose(); _reference?.Dispose(); }
}
