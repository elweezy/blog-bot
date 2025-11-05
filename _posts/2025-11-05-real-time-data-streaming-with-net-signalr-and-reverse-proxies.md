---
layout: post
title: "Real-time Data Streaming with .NET SignalR and Reverse Proxies"
date: 2025-11-05 17:30:26 +0000
categories: dotnet blog
canonical_url: "https://dev.to/morteza-jangjoo/real-time-streaming-in-net-signalr-live-data-like-trading-apps-45h4"
---

When you're building modern web applications, the demand for immediate, live feedback isn't just a nice-to-have anymore; it's practically a requirement. Think about it: a trading dashboard showing real-time price fluctuations, a live sports score tracker, a collaborative document editor, or even just a busy chat application. Users expect instant updates, and anything less feels… sluggish, even broken.

That's where technologies like SignalR come into play. It's Microsoft's elegant solution to real-time communication in .NET, abstracting away the complexities of WebSockets, long polling, and Server-Sent Events into a beautiful, easy-to-use API. But here's the thing: while SignalR makes the *development* part simple, deploying it for high-performance, real-time data streaming, especially behind reverse proxies, often unearths some interesting quirks. And trust me, I've spent more than a few late nights debugging connection issues that turned out to be a single missing header in an Nginx config.

### Why Real-time Streaming Matters More Than Ever (Especially in .NET 8/9)

The `.NET` ecosystem, particularly with Kestrel's insane performance and the ongoing focus on cloud-native deployments in .NET 8 and 9, is perfectly positioned for building these kinds of demanding applications. We're not just serving static pages or REST endpoints anymore; we're building interactive experiences that need to push data to potentially thousands, or even millions, of clients concurrently.

For data streaming, this isn't just about sending small messages. It's about a continuous flow of information – potentially high-volume – without constantly re-establishing connections or polling. WebSockets, the underlying technology SignalR prefers, are ideal for this persistent, bidirectional communication. And `.NET`'s support for WebSockets, coupled with SignalR's abstractions, makes it a joy to work with.

The "why now" isn't just about performance, though. It's also about developer experience (DX). With minimal APIs and source generators, we can spin up a real-time service incredibly fast. But without understanding the deployment nuances, that speed can turn into frustration when things hit production.

### Getting Started: SignalR Basics (and a Dash of Streaming)

At its core, SignalR uses "Hubs" to define methods that clients can call on the server, and methods that the server can call on connected clients. It manages all the underlying transport details.

Let's imagine we're building that real-time trading app. We need to stream stock price updates to anyone connected.

First, you'd set up your `Program.cs`:

```csharp
// Program.cs
using Microsoft.AspNetCore.SignalR;
using RealtimeStreamingApp.Hubs;
using RealtimeStreamingApp.Services;

var builder = WebApplication.CreateBuilder(args);

// Add SignalR to the service collection.
builder.Services.AddSignalR();
builder.Services.AddHostedService<StockPriceSimulator>(); // Our background service

var app = builder.Build();

app.UseRouting();

// Map our StockHub to a specific path
app.MapHub<StockHub>("/stockhub");

app.Run();
```

And then our `StockHub` itself:

```csharp
// Hubs/StockHub.cs
using Microsoft.AspNetCore.SignalR;

namespace RealtimeStreamingApp.Hubs;

// This is where clients connect and can call methods on the server.
// For streaming, the server will call methods on clients.
public class StockHub : Hub
{
    // A client could call this to subscribe to a specific stock.
    public async Task SubscribeToStock(string stockSymbol)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, stockSymbol);
        Console.WriteLine($"Client {Context.ConnectionId} subscribed to {stockSymbol}");
    }

    // A client could call this to unsubscribe.
    public async Task UnsubscribeFromStock(string stockSymbol)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, stockSymbol);
        Console.WriteLine($"Client {Context.ConnectionId} unsubscribed from {stockSymbol}");
    }

    public override async Task OnConnectedAsync()
    {
        Console.WriteLine($"Client connected: {Context.ConnectionId}");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        Console.WriteLine($"Client disconnected: {Context.ConnectionId}, Exception: {exception?.Message}");
        await base.OnDisconnectedAsync(exception);
    }
}
```

Now, the real magic for *streaming* data from the server happens when we use a background service to push data to the Hub:

```csharp
// Services/StockPriceSimulator.cs
using Microsoft.AspNetCore.SignalR;
using RealtimeStreamingApp.Hubs;
using System.Collections.Concurrent;

namespace RealtimeStreamingApp.Services;

public class StockPriceSimulator : BackgroundService
{
    private readonly IHubContext<StockHub> _hubContext;
    private readonly ILogger<StockPriceSimulator> _logger;
    private readonly ConcurrentDictionary<string, decimal> _currentPrices = new();
    private readonly Random _random = new();

    private static readonly string[] _stockSymbols = { "MSFT", "GOOG", "AAPL", "AMZN", "TSLA", "NVDA" };

    public StockPriceSimulator(IHubContext<StockHub> hubContext, ILogger<StockPriceSimulator> logger)
    {
        _hubContext = hubContext;
        _logger = logger;

        // Initialize some random starting prices
        foreach (var symbol in _stockSymbols)
        {
            _currentPrices[symbol] = (decimal)(_random.NextDouble() * 1000);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Stock price simulator starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var symbol in _stockSymbols)
            {
                // Simulate price change
                var currentPrice = _currentPrices[symbol];
                var change = (decimal)(_random.NextDouble() * 10 - 5); // -5 to +5
                var newPrice = currentPrice + change;

                // Keep prices positive and somewhat realistic
                if (newPrice < 1m) newPrice = 1m;

                _currentPrices[symbol] = newPrice;

                // Send update to clients in the specific stock group
                // Clients would implement a method called "ReceiveStockUpdate"
                await _hubContext.Clients.Group(symbol).SendAsync("ReceiveStockUpdate", symbol, newPrice, stoppingToken);
                await _hubContext.Clients.All.SendAsync("ReceiveStockUpdate", symbol, newPrice, stoppingToken); // Fallback for all clients
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); // Update every second
        }

        _logger.LogInformation("Stock price simulator stopping.");
    }
}

```

This `StockPriceSimulator` is a `BackgroundService` that every second generates new prices and uses `_hubContext.Clients.Group(symbol).SendAsync("ReceiveStockUpdate", ...)` to push these updates to all clients subscribed to that symbol. On the client side (e.g., JavaScript), you'd simply have a function called `ReceiveStockUpdate` that gets invoked. This is powerful, direct, real-time streaming.

### The Elephant in the Room: Reverse Proxies

Now, here's where things get interesting in production. You're almost certainly not exposing your Kestrel server directly to the internet. Instead, you'll have a reverse proxy sitting in front of it. This could be Nginx, HAProxy, Azure Application Gateway, AWS Application Load Balancer (ALB), or even IIS with ARR.

**Why reverse proxies?** Load balancing, SSL termination, caching, security, request routing, WAF (Web Application Firewall) capabilities – the list goes on. They're essential.

**The catch with SignalR and WebSockets:**
WebSockets start life as a standard HTTP request. The client sends an `Upgrade` header to the server, asking to switch protocols from HTTP/1.1 to WebSockets. The server, if it agrees, responds with a `101 Switching Protocols` status code and some specific headers, and boom – you have a persistent, bidirectional WebSocket connection.

The problem arises when your reverse proxy doesn't understand this "protocol upgrade" dance. If it treats it like a regular HTTP request, it might:
1.  **Strip or modify crucial headers:** The `Upgrade` and `Connection` headers are non-negotiable. If they're missing or malformed by the proxy, the upgrade fails.
2.  **Close connections prematurely:** WebSockets are long-lived. Proxies often have default timeouts for HTTP connections. If the proxy closes the connection before it even starts or after a short period, your SignalR clients will keep trying to reconnect.
3.  **Lack "sticky sessions":** If you have multiple SignalR servers behind a load balancer, and a client reconnects, it *must* ideally hit the same server it was on before (unless you've configured a backplane like Redis, which we'll touch on). While SignalR can usually recover, sticky sessions make things smoother.

#### Common Reverse Proxy Configurations

Let's look at Nginx, a popular choice, as an example:

```nginx
# Nginx configuration snippet for SignalR WebSockets
server {
    listen 80;
    server_name your-app.com;

    location / {
        # Proxy to your Kestrel application
        proxy_pass http://localhost:5000; # Or your internal Kestrel address

        # ESSENTIAL for WebSockets:
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "Upgrade"; # Must be "Upgrade", not "$connection" variable

        # Optional: X-Forwarded-For, X-Real-IP for correct client IP logging
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # Keep alive for WebSocket connections - adjust as needed
        proxy_read_timeout 86400s; # 24 hours, or longer if required
        proxy_send_timeout 86400s;
        proxy_buffering off; # Important for streaming

        # Optional: for sticky sessions (if using multiple SignalR servers without a backplane)
        # Check Nginx Plus or third-party modules for proper sticky sessions.
        # For simple round-robin, this isn't needed, but Redis backplane is better.
    }
}
```

The lines `proxy_set_header Upgrade $http_upgrade;` and `proxy_set_header Connection "Upgrade";` are the absolute heroes here. Without them, your WebSocket connections will simply not establish correctly. `proxy_buffering off;` is also vital for true real-time streaming, preventing Nginx from buffering responses before sending them to the client.

For other proxies:
*   **HAProxy:** You'd configure it for TCP mode and ensure proper `http-request set-header` rules for `Upgrade` and `Connection`.
*   **Azure App Gateway/AWS ALB:** These usually have built-in WebSocket support, but you still need to ensure your backend pools are configured correctly and that any WAF rules aren't blocking the WebSocket handshake. Timeouts are also a common gotcha here.

### Pitfalls, Gotchas, and Best Practices

1.  **Reverse Proxy Configuration (Again!):** I can't stress this enough. If your SignalR clients are constantly reconnecting or falling back to long-polling (which is far less efficient for streaming), *always* check your reverse proxy's WebSocket configuration first. Check logs on both the proxy and your .NET app.
2.  **Scale-Out with Backplanes:** Our current `StockPriceSimulator` only sends messages to clients connected to *that specific Kestrel instance*. If you deploy multiple instances of your .NET app behind a load balancer, clients connected to Server A won't receive updates from Server B. This is where a **SignalR backplane** comes in. The most common choice is **Redis**.
    ```csharp
    // In Program.cs, after AddSignalR()
    builder.Services.AddSignalR()
        .AddStackExchangeRedis("your_redis_connection_string");
    ```
    With Redis, when one server sends a message, it publishes it to Redis, which then broadcasts it to *all* connected SignalR servers, ensuring all clients (regardless of which server they're connected to) receive the message. Azure SignalR Service is another excellent option if you're in Azure, offloading all the connection management and scaling to a managed service.
3.  **Message Frequency and Size:** While SignalR handles a lot, you still need to be mindful. Are you sending updates every millisecond? Do you really need to? Can you batch updates, or send only the deltas (changes) instead of the full object every time? Overly chatty clients or massive messages can saturate network bandwidth and server resources.
4.  **Security (AuthN/AuthZ):** Don't forget to secure your Hubs! SignalR integrates seamlessly with ASP.NET Core's authentication and authorization mechanisms.
    ```csharp
    // In your StockHub.cs
    [Authorize] // Only authenticated users can connect
    public class StockHub : Hub
    {
        // ...
        [Authorize(Roles = "Admin")] // Only admins can call this method
        public async Task ForceStockUpdate(string symbol, decimal newPrice) { /* ... */ }
    }
    ```
5.  **Client-Side Resilience:** Network conditions are flaky. Ensure your client-side SignalR code has robust reconnection logic. The SignalR client SDKs usually handle this well out-of-the-box, but understanding its retry policies is crucial.
6.  **Connection Management and Cleanup:** If clients subscribe to groups (like our stock symbols), make sure they unsubscribe when they no longer need the updates or when they disconnect. Over time, stale connections or group memberships can accumulate resources.

### Wrapping Up

Building real-time data streaming applications with .NET SignalR is an absolute joy. It gives your applications that modern, interactive edge users expect. The framework handles so much of the heavy lifting, letting you focus on the business logic.

However, don't let that ease of development blind you to the operational realities, especially when deploying behind reverse proxies. Getting those proxy configurations right is half the battle, and once you layer on scaling requirements with a backplane, you've got a robust, high-performance real-time system that can handle serious traffic.

So, next time you're thinking about adding live data to your app, jump in with SignalR. Just remember to give your reverse proxy some love and attention – it'll save you a lot of headaches down the line. Happy streaming!
