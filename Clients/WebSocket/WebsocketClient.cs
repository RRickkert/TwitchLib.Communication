﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TwitchLib.Communication.Enums;
using TwitchLib.Communication.Events;

namespace TwitchLib.Communication
{
    public class WebSocketClient : IClient, IDisposable
    {
        private string Url { get; }
        private ClientWebSocket _ws;
        private readonly IClientOptions _options;
        private bool _disconnectCalled;
        private bool _listenerRunning;
        private bool _senderRunning;
        private bool _whisperSenderRunning;
        private bool _monitorRunning;
        private bool _reconnecting;
        private CancellationTokenSource _tokenSource = new CancellationTokenSource();
        private Task _monitor;
        private Task _listener;
        private Task _sender;
        private Task _whisperSender;
        private Throttlers _throttlers;

        /// <summary>
        /// The current state of the connection.
        /// </summary>
        public bool IsConnected => _ws == null ? false : _ws.State == WebSocketState.Open;

        /// <summary>
        /// The current number of items waiting to be sent.
        /// </summary>
        public int SendQueueLength => _throttlers.SendQueue.Count;

        /// <summary>
        /// The current number of Whispers waiting to be sent.
        /// </summary>
        public int WhisperQueueLength => _throttlers.WhisperQueue.Count;

        [EditorBrowsable(EditorBrowsableState.Never)]
        public TimeSpan DefaultKeepAliveInterval
        {
            get => _ws.Options.KeepAliveInterval;
            set => _ws.Options.KeepAliveInterval = value;
        }

        #region Events
        /// <summary>
        /// Fires when Data (ByteArray) is received.
        /// </summary>
        public event EventHandler<OnDataEventArgs> OnData;

        /// <summary>
        /// Fires when a Message/ group of messages is received.
        /// </summary>
        public event EventHandler<OnMessageEventArgs> OnMessage;

        /// <summary>
        /// Fires when the websocket state changes
        /// </summary>
        public event EventHandler<OnStateChangedEventArgs> OnStateChanged;

        /// <summary>
        /// Fires when the Client has connected
        /// </summary>
        public event EventHandler<OnConnectedEventArgs> OnConnected;

        /// <summary>
        /// Fires when the Client disconnects
        /// </summary>
        public event EventHandler<OnDisconnectedEventArgs> OnDisconnected;

        /// <summary>
        /// Fires when An Exception Occurs in the client
        /// </summary>
        public event EventHandler <OnErrorEventArgs> OnError;

        /// <summary>
        /// Fires when a message Send event failed.
        /// </summary>
        public event EventHandler <OnSendFailedEventArgs> OnSendFailed;

        /// <summary>
        /// Fires when a Fatal Error Occurs.
        /// </summary>
        public event EventHandler<OnFatalErrorEventArgs> OnFatality;

        /// <summary>
        /// Fires when a Message has been throttled.
        /// </summary>
        public event EventHandler<OnMessageThrottledEventArgs> OnMessageThrottled;

        /// <summary>
        /// Fires when a Whisper has been throttled.
        /// </summary>
        public event EventHandler<OnWhisperThrottledEventArgs> OnWhisperThrottled;
        #endregion

        public WebSocketClient(IClientOptions options = null)
        {
            _options = options ?? new ClientOptions();

            switch (_options.ClientType)
            {
                case ClientType.Chat:
                    Url = _options.UseSSL ? "wss://irc-ws.chat.twitch.tv:443" : "ws://irc-ws.chat.twitch.tv:80";
                    break;
                case ClientType.PubSub:
                    Url = _options.UseSSL ? "wss://pubsub-edge.twitch.tv:443" : "ws://pubsub-edge.twitch.tv:80";
                    break;
            }
            _throttlers = new Throttlers(_options.ThrottlingPeriod, _options.WhisperThrottlingPeriod);

            InitializeClient();
            StartMonitor();
        }

        private void InitializeClient()
        {
            _ws = new ClientWebSocket();

            DefaultKeepAliveInterval = Timeout.InfiniteTimeSpan;

            if (_options.Headers != null)
                foreach (var h in _options.Headers)
                {
                    try
                    {
                        _ws.Options.SetRequestHeader(h.Item1, h.Item2);
                    }
                    catch
                    { }
                }
        }

        /// <summary>
        /// Connect the Client to the requested Url.
        /// </summary>
        /// <returns>Returns True if Connected, False if Failed to Connect.</returns>
        public bool Open()
        {
            try
            {
                _disconnectCalled = false;
                _ws.ConnectAsync(new Uri(Url), _tokenSource.Token).Wait(15000);
                StartListener();
                StartSender();
                StartWhisperSender();
                _throttlers.StartThrottlingWindowReset();
                _throttlers.StartWhisperThrottlingWindowReset();

                Task.Run(() =>
                {
                    while (_ws.State != WebSocketState.Open)
                    { }
                }).Wait(15000);
                return _ws.State == WebSocketState.Open;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, new OnErrorEventArgs { Exception = ex });
                throw;
            }
        }

        /// <summary>
        /// Queue a Message to Send to the server as a String.
        /// </summary>
        /// <param name="data">The Message To Queue</param>
        /// <returns>Returns True if was successfully queued. False if it fails.</returns>
        public bool Send(string data)
        {
            try
            {
                if (!IsConnected || SendQueueLength >= _options.SendQueueCapacity)
                {
                    return false;
                }

                Task.Run(() =>
                {
                    _throttlers.SendQueue.Add(new Tuple<DateTime, string>(DateTime.UtcNow, data));
                }).Wait(100, _tokenSource.Token);

                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, new OnErrorEventArgs { Exception = ex });
                throw;
            }
        }

        /// <summary>
        /// Queue a Whisper to Send to the server as a String.
        /// </summary>
        /// <param name="data">The Whisper To Queue</param>
        /// <returns>Returns True if was successfully queued. False if it fails.</returns>
        public bool SendWhisper(string data)
        {
            try
            {
                if (!IsConnected || WhisperQueueLength >= _options.WhisperQueueCapacity)
                {
                    return false;
                }

                Task.Run(() =>
                {
                    _throttlers.WhisperQueue.Add(new Tuple<DateTime, string>(DateTime.UtcNow, data));
                }).Wait(100, _tokenSource.Token);

                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, new OnErrorEventArgs { Exception = ex });
                throw;
            }
        }

        private void StartMonitor()
        {
            _monitor = Task.Run(() =>
            {
                _monitorRunning = true;
                var needsReconnect = false;
                try
                {
                    var lastState = _ws.State == WebSocketState.Open ? true : false;
                    while (_ws != null && !_disposedValue)
                    {
                        if (lastState == (_ws.State == WebSocketState.Open ? true : false))
                        {
                            Thread.Sleep(200);
                            continue;
                        }
                        OnStateChanged?.Invoke(this, new OnStateChangedEventArgs { IsConnected = _ws.State == WebSocketState.Open, WasConnected = lastState});

                        if (_ws.State == WebSocketState.Open)
                            OnConnected?.Invoke(this, new OnConnectedEventArgs());

                        if ((_ws.State == WebSocketState.Closed || _ws.State == WebSocketState.Aborted) && !_reconnecting)
                        {
                            if (lastState && !_disconnectCalled && _options.ReconnectionPolicy != null && !_options.ReconnectionPolicy.AreAttemptsComplete())
                            {
                                needsReconnect = true;
                                break;
                            }
                            OnDisconnected?.Invoke(this, new OnDisconnectedEventArgs());
                            if (_ws.CloseStatus != null && _ws.CloseStatus != WebSocketCloseStatus.NormalClosure)
                                OnError?.Invoke(this, new OnErrorEventArgs { Exception = new Exception(_ws.CloseStatus + " " + _ws.CloseStatusDescription) });
                        }

                        lastState = _ws.State == WebSocketState.Open ? true : false;
                    }
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(this, new OnErrorEventArgs { Exception = ex });
                }
                if (needsReconnect && !_reconnecting && !_disconnectCalled)
                    DoReconnect();
                _monitorRunning = false;
            });
        }

        private Task DoReconnect()
        {
            return Task.Run(() =>
            {
                _tokenSource.Cancel();
                _reconnecting = true;
                _throttlers.Reconnecting = true;
                if (!Task.WaitAll(new[] {_monitor, _listener, _sender, _whisperSender, _throttlers.ResetThrottler, _throttlers.ResetWhisperThrottler }, 15000))
                {
                    OnFatality?.Invoke(this, new OnFatalErrorEventArgs { Reason = "Fatal network error. Network services fail to shut down." });
                    _reconnecting = false;
                    _throttlers.Reconnecting = false;
                    _disconnectCalled = true;
                    _tokenSource.Cancel();
                    return;
                }
                _ws.Dispose();

                OnStateChanged?.Invoke(this, new OnStateChangedEventArgs { IsConnected = false, WasConnected = false });

                _tokenSource = new CancellationTokenSource();

                var connected = false;
                while (!_disconnectCalled && !_disposedValue && !connected && !_tokenSource.IsCancellationRequested)
                    try
                    {
                        InitializeClient();
                        if (!_monitorRunning)
                        {
                            StartMonitor();
                        }
                        connected = _ws.ConnectAsync(new Uri(Url), _tokenSource.Token).Wait(15000);
                    }
                    catch
                    {
                        _ws.Dispose();
                        Thread.Sleep(_options.ReconnectionPolicy.GetReconnectInterval());
                        _options.ReconnectionPolicy.ProcessValues();
                        if (_options.ReconnectionPolicy.AreAttemptsComplete())
                        {
                            OnFatality?.Invoke(this, new OnFatalErrorEventArgs { Reason = "Fatal network error. Max reconnect attemps reached." });
                            _reconnecting = false;
                            _throttlers.Reconnecting = false;
                            _disconnectCalled = true;
                            _tokenSource.Cancel();
                            return;
                        }
                    }
                if (connected)
                {
                    _reconnecting = false;
                    _throttlers.Reconnecting = false;
                    if (!_monitorRunning)
                        StartMonitor();
                    if (!_listenerRunning)
                        StartListener();
                    if (!_senderRunning)
                        StartSender();
                    if (!_whisperSenderRunning)
                        StartWhisperSender();
                    if (!_throttlers.ResetThrottlerRunning)
                        _throttlers.StartThrottlingWindowReset();
                    if (_throttlers.ResetWhisperThrottlerRunning)
                        _throttlers.StartWhisperThrottlingWindowReset();
                }
            });
        }

        private void StartListener()
        {
            _listener = Task.Run(async () =>
            {
                _listenerRunning = true;
                try
                {
                    while (_ws.State == WebSocketState.Open && !_disposedValue && !_reconnecting)
                    {
                        var message = "";
                        var binary = new List<byte>();

                        READ:

                        var buffer = new byte[1024];
                        WebSocketReceiveResult res = null;

                        try
                        {
                            res = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _tokenSource.Token);
                        }
                        catch
                        {
                            _ws.Abort();
                            break;
                        }

                        if (res == null)
                            goto READ;

                        if (res.MessageType == WebSocketMessageType.Close)
                        {
                            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "SERVER REQUESTED CLOSE", _tokenSource.Token);
                        }

                        if (res.MessageType == WebSocketMessageType.Text)
                        {
                            if (!res.EndOfMessage)
                            {
                                message += Encoding.UTF8.GetString(buffer).TrimEnd('\0');
                                goto READ;
                            }
                            message += Encoding.UTF8.GetString(buffer).TrimEnd('\0');

                            if (message.Trim() == "ping")
                                Send("pong");
                            else
                            {
                                Task.Run(() => OnMessage?.Invoke(this, new OnMessageEventArgs { Message = message })).Wait(50);
                            }
                        }
                        else
                        {
                            if (!res.EndOfMessage)
                            {
                                binary.AddRange(buffer.Where(b => b != '\0'));
                                goto READ;
                            }

                            binary.AddRange(buffer.Where(b => b != '\0'));
                            Task.Run(() => OnData?.Invoke(this, new OnDataEventArgs { Data = binary.ToArray() })).Wait(50);
                        }
                        buffer = null;
                    }
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(this, new OnErrorEventArgs { Exception = ex });
                }
                _listenerRunning = false;
                return Task.CompletedTask;
            });
        }

        private void StartSender()
        {
            _sender = Task.Run(async () =>
            {
                _senderRunning = true;
                try
                {
                    while (!_disposedValue && !_reconnecting)
                    {
                        await Task.Delay(_options.SendDelay);

                        if (_throttlers.SentCount == _options.MessagesAllowedInPeriod)
                        {
                            OnMessageThrottled?.Invoke(this, new OnMessageThrottledEventArgs
                            {
                                Message = "Message Throttle Occured. Too Many Messages within the period specified in WebsocketClientOptions.",
                                AllowedInPeriod = _options.MessagesAllowedInPeriod,
                                Period = _options.ThrottlingPeriod,
                                SentMessageCount = Interlocked.CompareExchange(ref _throttlers.SentCount, 0, 0)
                            });

                            continue;
                        }

                        if (_ws.State == WebSocketState.Open && !_reconnecting)
                        {
                            var msg = _throttlers.SendQueue.Take(_tokenSource.Token);
                            if (msg.Item1.Add(_options.SendCacheItemTimeout) < DateTime.UtcNow)
                            {
                                continue;
                            }
                            var buffer = Encoding.UTF8.GetBytes(msg.Item2);
                            try
                            {
                                await _ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, _tokenSource.Token);
                                _throttlers.IncrementSentCount();
                            }
                            catch (Exception ex)
                            {
                                OnSendFailed?.Invoke(this, new OnSendFailedEventArgs { Data = msg.Item2, Exception = ex });
                                _ws.Abort();
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnSendFailed?.Invoke(this, new OnSendFailedEventArgs { Data = "", Exception = ex });
                    OnError?.Invoke(this, new OnErrorEventArgs { Exception = ex });
                }
                _senderRunning = false;
                return Task.CompletedTask;
            });
        }

        private void StartWhisperSender()
        {
            _whisperSender = Task.Run(async () =>
            {
                _whisperSenderRunning = true;
                try
                {
                    while (!_disposedValue && !_reconnecting)
                    {
                        await Task.Delay(_options.SendDelay);

                        if (_throttlers.WhispersSent == _options.WhispersAllowedInPeriod)
                        {
                            OnWhisperThrottled?.Invoke(this, new OnWhisperThrottledEventArgs
                            {
                                Message = "Whisper Throttle Occured. Too Many Whispers within the period specified in WebsocketClientOptions.",
                                AllowedInPeriod = _options.WhispersAllowedInPeriod,
                                Period = _options.WhisperThrottlingPeriod,
                                SentWhisperCount = Interlocked.CompareExchange(ref _throttlers.WhispersSent, 0, 0)
                            });

                            continue;
                        }

                        if (_ws.State == WebSocketState.Open && !_reconnecting)
                        {
                            var msg = _throttlers.WhisperQueue.Take(_tokenSource.Token);
                            if (msg.Item1.Add(_options.SendCacheItemTimeout) < DateTime.UtcNow)
                            {
                                continue;
                            }
                            var buffer = Encoding.UTF8.GetBytes(msg.Item2);
                            try
                            {
                                await _ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, _tokenSource.Token);
                                _throttlers.IncrementWhisperCount();
                            }
                            catch (Exception ex)
                            {
                                OnSendFailed?.Invoke(this, new OnSendFailedEventArgs { Data = msg.Item2, Exception = ex });
                                _ws.Abort();
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnSendFailed?.Invoke(this, new OnSendFailedEventArgs { Data = "", Exception = ex });
                    OnError?.Invoke(this, new OnErrorEventArgs { Exception = ex });
                }
                _whisperSenderRunning = false;
                return Task.CompletedTask;
            });
        }
        
        /// <summary>
        /// Disconnect the Client from the Server
        /// </summary>
        public void Close()
        {
            try
            {
                _disconnectCalled = true;
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "NORMAL SHUTDOWN", _tokenSource.Token).Wait(_options.DisconnectWait);
            }
            catch
            { }
        }

        #region IDisposable Support

        private bool _disposedValue;

        protected virtual void Dispose(bool disposing, bool waitForSendsToComplete)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    if (_throttlers.SendQueue.Count > 0 && _senderRunning)
                    {
                        var i = 0;
                        while (_throttlers.SendQueue.Count > 0 && _senderRunning)
                        {
                            i++;
                            Task.Delay(1000).Wait();
                            if(i > 25)
                                break;
                        }
                    }
                    if (_throttlers.WhisperQueue.Count > 0 && _whisperSenderRunning)
                    {
                        var i = 0;
                        while (_throttlers.WhisperQueue.Count > 0 && _whisperSenderRunning)
                        {
                            i++;
                            Task.Delay(1000).Wait();
                            if (i > 25)
                                break;
                        }
                    }
                    Close();
                    _tokenSource.Cancel();
                    Thread.Sleep(500);
                    _tokenSource.Dispose();
                    _ws.Dispose();
                    GC.Collect();
                }

                _disposedValue = true;
                _throttlers.ShouldDispose = true;
            }
        }

        /// <summary>
        /// Dispose the Client. Forces the Send Queue to be destroyed, resulting in Message Loss.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }


        /// <summary>
        /// Disposes the Client. Waits for current Messages in the Queue to be processed first.
        /// </summary>
        /// <param name="waitForSendsToComplete">Should wait or not.</param>
        public void Dispose(bool waitForSendsToComplete)
        {
            Dispose(true, waitForSendsToComplete);
        }

        #endregion
    }
}