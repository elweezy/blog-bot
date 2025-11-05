// See https://aka.ms/new-console-template for more information

using BlogBot.Models;
using BlogBot.OpenAI;
using BlogBot.Repository;
using BlogBot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// 1. Configure options (you can later bind from appsettings.json if you want)
builder.Services.Configure<TopicDiscoveryOptions>(opts =>
{
    opts.StackExchangeSite = "stackoverflow";
    opts.StackOverflowTags = ".net;c#";
    opts.StackExchangeKey = null; // set if you register a SE API key
    opts.StackOverflowPageSize = 20;
    opts.DevToTag = "dotnet";
    opts.DevToPageSize = 20;
    opts.RedditSubreddit = "dotnet";
    opts.RedditLimit = 20;
    opts.MaxCombinedCandidates = 50;
});

// 2. HttpClient for TopicDiscoveryService (typed client)
builder.Services.AddHttpClient<TopicDiscoveryService>(client =>
{
    // Good practice: identify yourself to Reddit & others
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "blog-bot/1.0 (+https://github.com/elweezy/blog-bot)");
});

// 3. OpenAI client (currently using the mock; swap later with openai api client)
//builder.Services.AddSingleton<IOpenAIClient, MockOpenAiClient>();
builder.Services.AddHttpClient<GeminiClient>();
builder.Services.AddSingleton<IOpenAIClient,GeminiClient>();

// 4. FileBlogRepository – using environment to get repo root path
builder.Services.AddSingleton<FileBlogRepository>(sp =>
{
    var env = sp.GetRequiredService<IHostEnvironment>();
    var root = env.ContentRootPath; // this is repo root when run from GH Actions

    var postsDir = Path.Combine(root, "_posts");
    var historyPath = Path.Combine(root, "meta", "topics_history.json");

    return new FileBlogRepository(postsDir, historyPath);
});

// 5. BlogWriterService – main orchestrator
builder.Services.AddSingleton<BlogWriterService>();

var host = builder.Build();

// optional: support Ctrl+C cancellation
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var scope = host.Services.CreateScope();
var blogWriter = scope.ServiceProvider.GetRequiredService<BlogWriterService>();

await blogWriter.RunAsync(cts.Token);

Console.WriteLine("Blog generation completed.");
