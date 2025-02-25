// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Xunit;
using Xunit.Abstractions;
using Yarp.ReverseProxy.Common;

namespace Yarp.ReverseProxy;

public class WebSocketTests
{
    private readonly ITestOutputHelper _output;

    public WebSocketTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(WebSocketMessageType.Binary)]
    [InlineData(WebSocketMessageType.Text)]
    public async Task WebSocketMessageTypes(WebSocketMessageType messageType)
    {
        using var cts = CreateTimer();

        var test = CreateTestEnvironment();

        await test.Invoke(async uri =>
        {
            using var client = new ClientWebSocket();
            var webSocketsTarget = uri.Replace("https://", "wss://").Replace("http://", "ws://");
            var targetUri = new Uri(new Uri(webSocketsTarget, UriKind.Absolute), "websockets");
            await client.ConnectAsync(targetUri, cts.Token);

            var buffer = new byte[1024];
            var textToSend = $"Hello World!";
            var numBytes = Encoding.UTF8.GetBytes(textToSend, buffer.AsSpan());
            await client.SendAsync(new ArraySegment<byte>(buffer, 0, numBytes),
                messageType,
                endOfMessage: true,
                cts.Token);

            var message = await client.ReceiveAsync(buffer, cts.Token);

            Assert.Equal(messageType, message.MessageType);
            Assert.True(message.EndOfMessage);

            var text = Encoding.UTF8.GetString(buffer.AsSpan(0, message.Count));
            Assert.Equal(textToSend, text);

            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", cts.Token);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, client.CloseStatus);
            Assert.Equal("Bye", client.CloseStatusDescription);
        }, cts.Token);
    }

    [Fact]
    public async Task RawUpgradeTest()
    {
        using var cts = CreateTimer();

        var test = CreateTestEnvironment();

        await test.Invoke(async uri =>
        {
            using var client = WebSocketTests.CreateInvoker();
            var targetUri = new Uri(new Uri(uri, UriKind.Absolute), "rawupgrade");
            using var request = new HttpRequestMessage(HttpMethod.Get, targetUri);

            // TODO: https://github.com/microsoft/reverse-proxy/issues/255 Until this is fixed the "Upgrade: WebSocket" header is required.
            request.Headers.TryAddWithoutValidation("Upgrade", "WebSocket");

            request.Headers.TryAddWithoutValidation("Connection", "upgrade");
            request.Version = new Version(1, 1);

            var response = await client.SendAsync(request, cts.Token);

            Assert.Equal(HttpStatusCode.SwitchingProtocols, response.StatusCode);

            using var rawStream = await response.Content.ReadAsStreamAsync(cts.Token);

            var buffer = new byte[5];
            for (var i = 0; i <= 255; i++)
            {
                buffer[0] = (byte)i;
                await rawStream.WriteAsync(buffer, 0, buffer.Length, cts.Token);
                var read = await rawStream.ReadAsync(buffer, cts.Token);

                Assert.Equal(buffer.Length, read);
                Assert.Equal(i, buffer[0]);
            }

            await rawStream.WriteAsync(Encoding.UTF8.GetBytes("close"));
            while (await rawStream.ReadAsync(buffer, cts.Token) != 0) { }
            rawStream.Dispose();
        }, cts.Token);
    }

    [Fact]
    // https://github.com/microsoft/reverse-proxy/issues/255 IIS claims all requests are upgradeable.
    public async Task FalseUpgradeTest()
    {
        using var cts = CreateTimer();

        var test = CreateTestEnvironment(forceUpgradable: true);

        await test.Invoke(async uri =>
        {
            using var client = WebSocketTests.CreateInvoker();
            var targetUri = new Uri(new Uri(uri, UriKind.Absolute), "post");
            using var request = new HttpRequestMessage(HttpMethod.Post, targetUri);
            request.Content = new StringContent("Hello World");
            request.Version = new Version(1, 1);

            var response = await client.SendAsync(request, cts.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Hello World", await response.Content.ReadAsStringAsync(cts.Token));
        }, cts.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WebSocket11_To_11(bool useHttps)
    {
        using var cts = CreateTimer();

        var test = CreateTestEnvironment();
        test.ProxyProtocol = HttpProtocols.Http1;
        test.DestinationProtocol = HttpProtocols.Http1;
        test.DestionationHttpVersion = HttpVersion.Version11;
        test.UseHttpsOnProxy = useHttps;
        test.UseHttpsOnDestination = useHttps;

        await test.Invoke(async uri =>
        {
            using var client = new ClientWebSocket();
            await SendWebSocketRequestAsync(client, uri, "HTTP/1.1", cts.Token);
        }, cts.Token);
    }

#if NET7_0_OR_GREATER
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WebSocket20_To_20(bool useHttps)
    {
        if (OperatingSystem.IsMacOS() && useHttps)
        {
            // Does not support ALPN until .NET 8
            return;
        }

        using var cts = CreateTimer();

        var test = CreateTestEnvironment();
        test.ProxyProtocol = HttpProtocols.Http2;
        test.DestinationProtocol = HttpProtocols.Http2;
        test.DestionationHttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        test.UseHttpsOnProxy = useHttps;
        test.UseHttpsOnDestination = useHttps;

        await test.Invoke(async uri =>
        {
            using var client = new ClientWebSocket();
            client.Options.HttpVersion = HttpVersion.Version20;
            client.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            await SendWebSocketRequestAsync(client, uri, "HTTP/2", cts.Token);
        }, cts.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WebSocket20_To_11(bool useHttps)
    {
        if (OperatingSystem.IsMacOS() && useHttps)
        {
            // Does not support ALPN until .NET 8
            return;
        }

        using var cts = CreateTimer();

        var test = CreateTestEnvironment();
        test.ProxyProtocol = HttpProtocols.Http2;
        test.DestinationProtocol = HttpProtocols.Http1;
        test.DestionationHttpVersion = HttpVersion.Version11;
        test.UseHttpsOnProxy = useHttps;
        test.UseHttpsOnDestination = useHttps;

        await test.Invoke(async uri =>
        {
            using var client = new ClientWebSocket();
            client.Options.HttpVersion = HttpVersion.Version20;
            client.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            await SendWebSocketRequestAsync(client, uri, "HTTP/1.1", cts.Token);
        }, cts.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WebSocket11_To_20(bool useHttps)
    {
        if (OperatingSystem.IsMacOS() && useHttps)
        {
            // Does not support ALPN until .NET 8
            return;
        }

        using var cts = CreateTimer();

        var test = CreateTestEnvironment();
        test.ProxyProtocol = HttpProtocols.Http1;
        test.DestinationProtocol = HttpProtocols.Http2;
        test.DestionationHttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        test.UseHttpsOnProxy = useHttps;
        test.UseHttpsOnDestination = useHttps;

        await test.Invoke(async uri =>
        {
            using var client = new ClientWebSocket();
            client.Options.HttpVersion = HttpVersion.Version11;
            client.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            await SendWebSocketRequestAsync(client, uri, "HTTP/2", cts.Token);
        }, cts.Token);
    }

    [Fact]
    public async Task WebSocketFallbackFromH2()
    {
        using var cts = CreateTimer();

        var test = CreateTestEnvironment();
        test.ProxyProtocol = HttpProtocols.Http1;
        // The destination doesn't support HTTP/2, as determined by ALPN
        test.DestinationProtocol = HttpProtocols.Http1;
        test.DestionationHttpVersion = HttpVersion.Version20;
        test.UseHttpsOnDestination = true;

        await test.Invoke(async uri =>
        {
            using var client = new ClientWebSocket();
            await SendWebSocketRequestAsync(client, uri, "HTTP/1.1", cts.Token);
        }, cts.Token);
    }

    // [Fact]
    [Fact(Skip = "Manual test only, the CI doesn't always have the IIS Express test cert installed.")]
    public async Task WebSocketFallbackFromH2WS()
    {
        if (!OperatingSystem.IsWindows())
        {
            // This test relies on Windows/HttpSys
            return;
        }

        using var cts = CreateTimer();

        var test = CreateTestEnvironment();
        test.ProxyProtocol = HttpProtocols.Http1;
        // The destination supports HTTP/2, but not H2WS
        test.UseHttpSysOnDestination = true;
        test.UseHttpsOnDestination = true;

        await test.Invoke(async uri =>
        {
            using var client = new ClientWebSocket();
            await SendWebSocketRequestAsync(client, uri, "HTTP/1.1", cts.Token);
        }, cts.Token);
    }

    [Theory]
    [InlineData(HttpVersionPolicy.RequestVersionExact, true)]
#if NET8_0_OR_GREATER
    [InlineData(HttpVersionPolicy.RequestVersionExact, false)] // Times out. https://github.com/dotnet/runtime/issues/80056
#endif
    [InlineData(HttpVersionPolicy.RequestVersionOrHigher, true)]
    public async Task WebSocketCantFallbackFromH2(HttpVersionPolicy policy, bool useHttps)
    {
        using var cts = CreateTimer();

        var test = CreateTestEnvironment();
        test.ProxyProtocol = HttpProtocols.Http1;
        test.DestinationProtocol = HttpProtocols.Http1;
        test.DestionationHttpVersion = HttpVersion.Version20;
        test.DestionationHttpVersionPolicy = policy;
        test.UseHttpsOnDestination = useHttps;

        await test.Invoke(async uri =>
        {
            using var client = new ClientWebSocket();
            var webSocketsTarget = uri.Replace("https://", "wss://").Replace("http://", "ws://");
            var targetUri = new Uri(new Uri(webSocketsTarget, UriKind.Absolute), "websockets");
#if NET7_0_OR_GREATER
            using var invoker = CreateInvoker();
            var wse = await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync(targetUri, invoker, cts.Token));
#else
            client.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            var wse = await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync(targetUri, cts.Token));
#endif
            Assert.Equal("The server returned status code '502' when status code '101' was expected.", wse.Message);
        }, cts.Token);
    }
#endif

    [Theory]
    [InlineData(HttpProtocols.Http1)] // Checked by destination
#if NET7_0_OR_GREATER
    [InlineData(HttpProtocols.Http2)] // Checked by proxy
#endif
    public async Task InvalidKeyHeader_400(HttpProtocols destinationProtocol)
    {
        using var cts = CreateTimer();

        var test = CreateTestEnvironment();
        test.ProxyProtocol = HttpProtocols.Http1;
        test.DestinationProtocol = destinationProtocol;
        test.DestionationHttpVersion = HttpVersion.Version20;
        test.DestionationHttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;

        test.ConfigureProxyApp = builder =>
        {
            builder.Use((context, next) =>
            {
                context.Request.Headers[HeaderNames.SecWebSocketKey] = "ThisIsAnIncorrectKeyHeaderLongerThan24Bytes";
                return next(context);
            });
        };

        await test.Invoke(async uri =>
        {
            using var client = new ClientWebSocket();
#if NET7_0_OR_GREATER
            client.Options.CollectHttpResponseDetails = true;
#endif
            var webSocketsTarget = uri.Replace("https://", "wss://").Replace("http://", "ws://");
            var targetUri = new Uri(new Uri(webSocketsTarget, UriKind.Absolute), "websockets");
            client.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            var wse = await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync(targetUri, cts.Token));
            Assert.Equal("The server returned status code '400' when status code '101' was expected.", wse.Message);
#if NET7_0_OR_GREATER
            Assert.Equal(HttpStatusCode.BadRequest, client.HttpStatusCode);
#endif
            // TODO: Assert the version https://github.com/dotnet/runtime/issues/75353
        }, cts.Token);
    }

    private async Task SendWebSocketRequestAsync(ClientWebSocket client, string uri, string destinationProtocol, CancellationToken token)
    {
        var webSocketsTarget = uri.Replace("https://", "wss://").Replace("http://", "ws://");
        var targetUri = new Uri(new Uri(webSocketsTarget, UriKind.Absolute), "websocketversion");
#if NET7_0_OR_GREATER
        using var invoker = CreateInvoker();
        await client.ConnectAsync(targetUri, invoker, token);
#else
        client.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await client.ConnectAsync(targetUri, token);
#endif
        _output.WriteLine("Client connected.");

        var buffer = new byte[1024];
        var textToSend = $"Hello World!";
        var numBytes = Encoding.UTF8.GetBytes(textToSend, buffer.AsSpan());
        await client.SendAsync(new ArraySegment<byte>(buffer, 0, numBytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            token);
        _output.WriteLine($"Client sent {numBytes}.");

        var message = await client.ReceiveAsync(buffer, token);
        _output.WriteLine($"Client received {message.Count}.");

        Assert.Equal(WebSocketMessageType.Text, message.MessageType);
        Assert.True(message.EndOfMessage);

        var text = Encoding.UTF8.GetString(buffer.AsSpan(0, message.Count));
        Assert.Equal(destinationProtocol, text);

        _output.WriteLine($"Client sending Close.");
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", token);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, client.CloseStatus);
        Assert.Equal("Bye", client.CloseStatusDescription);
        _output.WriteLine($"Client Closed.");
    }

    private TestEnvironment CreateTestEnvironment(bool forceUpgradable = false)
    {
        return new TestEnvironment()
        {
            TestOutput = _output,
            ConfigureDestinationServices = destinationServies =>
            {
                destinationServies.AddRouting();
            },
            ConfigureDestinationApp = destinationApp =>
            {
                destinationApp.UseWebSockets();
                destinationApp.UseRouting();
                destinationApp.UseEndpoints(builder =>
                {
                    builder.Map("/websockets", WebSocket);
                    builder.Map("/websocketVersion", WebSocketVersion);
                    builder.Map("/rawupgrade", RawUpgrade);
                    builder.Map("/post", Post);
                });
            },
            ConfigureProxyApp = proxyApp =>
            {
                // Mimic the IIS issue https://github.com/microsoft/reverse-proxy/issues/255
                proxyApp.Use((context, next) =>
                {
                    if (forceUpgradable && !(context.Features.Get<IHttpUpgradeFeature>()?.IsUpgradableRequest == true))
                    {
                        context.Features.Set<IHttpUpgradeFeature>(new AlwaysUpgradeFeature());
                    }
                    return next();
                });
            },
        };

        static async Task WebSocket(HttpContext httpContext)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILogger<WebSocketTests>>();
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                logger.LogInformation("Non-WebSocket request refused.");
                return;
            }

            using var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();
            logger.LogInformation("WebSocket accepted.");

            var buffer = new byte[1024];
            while (true)
            {
                var message = await webSocket.ReceiveAsync(buffer, httpContext.RequestAborted);
                if (message.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogInformation("WebSocket Close received {status}.", message.CloseStatus);
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, message.CloseStatusDescription, httpContext.RequestAborted);
                    logger.LogInformation("WebSocket Close sent {status}.", WebSocketCloseStatus.NormalClosure);
                    return;
                }

                logger.LogInformation("WebSocket received {count} bytes.", message.Count);

                await webSocket.SendAsync(buffer[0..message.Count],
                    message.MessageType,
                    message.EndOfMessage,
                    httpContext.RequestAborted);

                logger.LogInformation("WebSocket sent {count} bytes.", message.Count);
            }
        }

        static async Task WebSocketVersion(HttpContext httpContext)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILogger<WebSocketTests>>();
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                logger.LogInformation("Non-WebSocket request refused.");
                return;
            }

            using var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();
            logger.LogInformation("WebSocket accepted.");

            var buffer = new byte[1024];
            while (true)
            {
                var message = await webSocket.ReceiveAsync(buffer, httpContext.RequestAborted);
                if (message.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogInformation("WebSocket Close received {status}.", message.CloseStatus);
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, message.CloseStatusDescription, httpContext.RequestAborted);
                    logger.LogInformation("WebSocket Close sent {status}.", WebSocketCloseStatus.NormalClosure);
                    return;
                }

                logger.LogInformation("WebSocket received {count} bytes.", message.Count);

                await webSocket.SendAsync(Encoding.ASCII.GetBytes(httpContext.Request.Protocol),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    httpContext.RequestAborted);

                logger.LogInformation("WebSocket sent {count} bytes.", httpContext.Request.Protocol.Length);
            }
        }

        static async Task RawUpgrade(HttpContext httpContext)
        {
            var upgradeFeature = httpContext.Features.Get<IHttpUpgradeFeature>();
            if (upgradeFeature is null || !upgradeFeature.IsUpgradableRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                return;
            }

            await using var stream = await upgradeFeature.UpgradeAsync();
            var buffer = new byte[5];
            int read;
            while ((read = await stream.ReadAsync(buffer, httpContext.RequestAborted)) != 0)
            {
                await stream.WriteAsync(buffer, 0, read, httpContext.RequestAborted);

                if (string.Equals("close", Encoding.UTF8.GetString(buffer, 0, read), StringComparison.Ordinal))
                {
                    break;
                }
            }
        }

        static async Task Post(HttpContext httpContext)
        {
            var body = await new StreamReader(httpContext.Request.Body).ReadToEndAsync();
            await httpContext.Response.WriteAsync(body);
        }
    }

    private static CancellationTokenSource CreateTimer()
    {
        if (Debugger.IsAttached)
        {
            return new CancellationTokenSource();
        }
        return new CancellationTokenSource(TimeSpan.FromSeconds(15));
    }

    private static HttpMessageInvoker CreateInvoker()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return new HttpMessageInvoker(handler);
    }

    private class AlwaysUpgradeFeature : IHttpUpgradeFeature
    {
        public bool IsUpgradableRequest => true;

        public Task<Stream> UpgradeAsync()
        {
            throw new InvalidOperationException("This wasn't supposed to get called.");
        }
    }
}
