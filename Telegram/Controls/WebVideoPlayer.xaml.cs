﻿using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Td.Api;
using Telegram.ViewModels.Gallery;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Storage.Streams;
using Windows.UI.Xaml;

namespace Telegram.Controls
{
    public sealed partial class WebVideoPlayer : VideoPlayerBase
    {
        private CoreWebView2 _core;
        private GalleryMedia _video;

        private readonly Dictionary<int, AlternativeVideo> _playlist = new();

        private long _initialPosition;

        public WebVideoPlayer()
        {
            InitializeComponent();
            Disconnected += OnDisconnected;
        }

        private void OnDisconnected(object sender, RoutedEventArgs e)
        {
            if (_core != null)
            {
                _core.WebResourceRequested -= OnWebResourceRequested;
                _core.WebMessageReceived -= OnWebMessageReceived;

                _core.Stop();
                _core = null;
            }

            Video.Close();
        }

        public override void Play(GalleryMedia video, double position)
        {
            _video = video;

            foreach (var item in video.FindAlternatives("h264"))
            {
                _playlist[item.HlsFile.Id] = item;
            }

            if (_core == null)
            {
                _ = Video.EnsureCoreWebView2Async();
            }
            else
            {
                _core.Navigate("http://127.0.0.1/hls.html");
            }
        }

        public override void Play()
        {
            ExecuteScript("playerPlay();");
        }

        public override void Pause()
        {
            ExecuteScript("playerPause();");
        }

        public override void Toggle()
        {
            ExecuteScript("playerToggle()");
        }

        public override void Stop()
        {
            _core?.NavigateToString(string.Empty);
            OnClosed();
        }

        public override void Clear()
        {
            _core?.NavigateToString(string.Empty);
        }

        public override void AddTime(double value)
        {
            //_core?.AddTime((long)value);
        }

        private double _position;
        public override double Position
        {
            get => _position;
            set
            {
                OnPositionChanged(_position = value);
                ExecuteScript($"playerSeek({value})");
            }
        }

        private double _duration;
        public override double Duration
        {
            get => _duration;
        }

        private bool _isPlaying;
        public override bool IsPlaying
        {
            get => _isPlaying;
        }

        private double _volume = 1;
        public override double Volume
        {
            get => _volume;
            set
            {
                //_core.Volume = (int)(value * 100);
                OnVolumeChanged(_volume = value);
                ExecuteScript($"playerSetVolume({value})");
            }
        }

        private double _rate = 1;
        public override double Rate
        {
            get => _rate;
            set
            {
                _rate = value;
                ExecuteScript($"playerSetBaseRate({value})");
                //_core.Rate = (float)(value);
                //OnRateChanged(value);
            }
        }

        private bool _mute;
        public override bool Mute
        {
            get => _mute;
            set
            {
                _mute = value;
                ExecuteScript($"playerSetIsMuted({(value ? 1 : 0)})");
                //_core.Mute = value;
                //OnMuteChanged(value);
            }
        }

        private VideoPlayerLevel _currentLevel;
        public override VideoPlayerLevel CurrentLevel
        {
            get => _currentLevel;
            set
            {
                IsCurrentLevelAuto = value == null;
                OnLevelsChanged(Levels, _currentLevel = value);
                ExecuteScript($"playerSetLevel({value?.Index ?? -1})");
            }
        }

        private void OnInitialized(WebView2 sender, CoreWebView2InitializedEventArgs e)
        {
            _core = sender.CoreWebView2;
            _core.NavigationCompleted += OnNavigationCompleted;
            _core.WebResourceRequested += OnWebResourceRequested;
            _core.WebMessageReceived += OnWebMessageReceived;

            _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            _core.Navigate("http://127.0.0.1/hls.html");
        }

        private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            ExecuteScript("playerInitialize({debug:false});playerPlay();");
        }

        private async void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            var deferral = args.GetDeferral();

            var segments = args.Request.Uri.Split('/');
            var resource = segments[^1];

            Logger.Info(args.Request.Uri);

            async Task<IRandomAccessStream> ToStreamAsync(string source)
            {
                var stream = new InMemoryRandomAccessStream();
                using var writer = new DataWriter(stream.GetOutputStreamAt(0));

                var bytes = Encoding.ASCII.GetBytes(source);

                writer.WriteBytes(bytes);
                await writer.StoreAsync();

                return stream;
            }

            void CreateWebResourceResponse(IRandomAccessStream Content, int StatusCode, string ReasonPhrase, string Headers)
            {
                try
                {
                    if (IsConnected && _core != null)
                    {
                        args.Response = sender.Environment.CreateWebResourceResponse(Content, StatusCode, ReasonPhrase, Headers);
                    }
                }
                catch
                {
                    // All bla bla bla
                }
            }

            var fileName = System.IO.Path.GetFileNameWithoutExtension(resource);
            var extension = System.IO.Path.GetExtension(resource);

            if (resource == "hls.html" || resource == "hls.js")
            {
                var file = await Package.Current.InstalledLocation.GetFileAsync("Assets\\" + resource);

                using (var stream = await file.OpenReadAsync())
                {
                    CreateWebResourceResponse(stream, 200, "OK", "Content-Type: " + file.ContentType);
                }
            }
            else if (resource == "master.m3u8")
            {
                var playlistString = new StringBuilder();
                playlistString.Append("#EXTM3U\n");

                foreach (var item in _playlist.Values.OrderBy(x => x.Width * x.Height))
                {
                    int width = item.Width;
                    int height = item.Height;
                    int bandwidth = (int)((double)item.Video.Size / _video.Duration) * 8;

                    playlistString.Append($"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth},RESOLUTION={width}x{height}\n");
                    playlistString.Append($"{item.HlsFile.Id}.m3u8\n");
                }

                using (var stream = await ToStreamAsync(playlistString.ToString()))
                {
                    CreateWebResourceResponse(stream, 200, "OK", "Content-Type: application/vnd.apple.mpegurl");
                }
            }
            else if (int.TryParse(fileName, out int fileId))
            {
                long offset = 0;
                long limit = 0;

                if (args.Request.Headers.Contains("Range"))
                {
                    var range = args.Request.Headers.GetHeader("Range");
                    if (range != null && RangeHeaderValue.TryParse(range, out var ranges))
                    {
                        foreach (var part in ranges.Ranges)
                        {
                            offset = part.From ?? 0;
                            limit = part.To ?? -1;

                            limit = limit - offset + 1;
                            break;
                        }
                    }
                }

                Debug.WriteLine(resource + ", offset: " + offset + ", length:" + limit);

                var file = _video.ClientService.GetFileAsync(fileId).Result;
                var remote = new Telegram.Streams.RemoteFileSource(_video.ClientService, file, extension == ".m3u8" ? 32 : 31, true);

                if (limit == 0)
                {
                    limit = file.Size - offset;
                }

                remote.SeekCallback(offset);
                await remote.ReadCallbackAsync(limit);

                if (extension == ".m3u8")
                {
                    var text = await System.IO.File.ReadAllTextAsync(file.Local.Path);
                    var playlist = Regex.Replace(text, "mtproto:\\d+\\b", _playlist[fileId].Video.Id + ".mp4");

                    using (var stream = await ToStreamAsync(playlist))
                    {
                        CreateWebResourceResponse(stream, 200, "OK", "Content-Type: application/vnd.apple.mpegurl");
                    }
                }
                else
                {
                    // TODO: would be probably better to use Storage APIs as they're asynchronous
                    // At the same time, they're known to be slow, and they also seem to be quite buggy.
                    using (var stream = new System.IO.FileStream(file.Local.Path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                    {
                        stream.Seek(offset, System.IO.SeekOrigin.Begin);

                        byte[] buffer = new byte[(int)limit];
                        await stream.ReadAsync(buffer, 0, buffer.Length);

                        using var memory = new InMemoryRandomAccessStream();
                        using var writer = new DataWriter(memory.GetOutputStreamAt(0));

                        writer.WriteBytes(buffer);
                        await writer.StoreAsync();

                        CreateWebResourceResponse(memory, 206, "OK", "Content-Type: video/mp4");
                    }
                }
            }

            deferral.Complete();
        }

        private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (JsonObject.TryParse(args.WebMessageAsJson, out JsonObject data))
            {
                var eventName = data.GetNamedString("event", string.Empty);
                var eventData = data.GetNamedObject("data", new JsonObject());

                if (eventName == "playerStatus")
                {
                    var isReady = eventData.GetNamedBoolean("isReady", false);
                    OnFirstFrameReady(_isFirstFrameReady = eventData.GetNamedBoolean("isFirstFrameReady", false));
                    OnIsPlayingChanged(_isPlaying = eventData.GetNamedBoolean("isPlaying", false));
                    _rate = eventData.GetNamedNumber("rate", 1);
                    var defaultRate = eventData.GetNamedNumber("defaultRate", 1);
                    OnVolumeChanged(_volume = eventData.GetNamedNumber("volume", 1));
                    OnDurationChanged(_duration = eventData.GetNamedNumber("duration", 0));

                    var levels = eventData.GetNamedArray("levels", new JsonArray());
                    var currentLevel = (int)eventData.GetNamedNumber("currentLevel", -1);

                    var mapped = new List<VideoPlayerLevel>();

                    foreach (var level in levels.Select(x => x.GetObject()))
                    {
                        var index = (int)level.GetNamedNumber("index", 0);
                        var bitrate = (int)level.GetNamedNumber("bitrate", 100000);
                        var width = (int)level.GetNamedNumber("width", 1280);
                        var height = (int)level.GetNamedNumber("height", 720);

                        mapped.Add(new VideoPlayerLevel(index, bitrate, width, height));
                    }

                    OnLevelsChanged(mapped, _currentLevel = mapped.Count == 0 || currentLevel == -1 ? null : mapped[currentLevel]);
                }
                else if (eventName == "playerCurrentTime")
                {
                    OnPositionChanged(_position = eventData.GetNamedNumber("value", 0));
                }
            }
        }

        private void ExecuteScript(string script)
        {
            if (_core != null)
            {
                _ = _core.ExecuteScriptAsync(script);
            }
        }
    }
}