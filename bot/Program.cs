using BlogBot.Llm;
using BlogBot.Models;
using BlogBot.Repository;
using BlogBot.Services;
using BlogBot.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

// 1. Topic sources (defaults live in TopicDiscoveryOptions)
builder.Services.Configure<TopicDiscoveryOptions>(_ => { });

// 2. HttpClient for TopicDiscoveryService (typed client)
builder.Services.AddHttpClient<TopicDiscoveryService>(client =>
{
    // Identify the bot; Reddit and others reject anonymous clients.
    client.DefaultRequestHeaders.UserAgent.ParseAdd("blog-bot/2.0 (+https://github.com/elweezy/blog-bot)");
    client.Timeout = TimeSpan.FromSeconds(30);
    // A single page or feed never needs more than this; anything larger is skipped instead of buffered.
    client.MaxResponseContentBufferSize = 4 * 1024 * 1024;
}).ConfigurePrimaryHttpMessageHandler(SafeHttp.CreateHandler);

// 3. LLM client: Gemini free tier, or an offline mock for local testing
if (Environment.GetEnvironmentVariable("USE_MOCK_LLM") == "1")
{
    builder.Services.AddSingleton<ILlmClient, MockLlmClient>();
}
else
{
    // Thinking models can take a while on long posts.
    builder.Services.AddHttpClient<ILlmClient, GeminiClient>(client => client.Timeout = TimeSpan.FromMinutes(5));
}

// 4. FileBlogRepository: the bot runs from /bot, so the repo root is its parent
builder.Services.AddSingleton<FileBlogRepository>(sp =>
{
    var contentRoot = sp.GetRequiredService<IHostEnvironment>().ContentRootPath;
    var repoRoot = Directory.GetParent(contentRoot)?.FullName ?? contentRoot;

    var postsDir = Path.Combine(repoRoot, "_posts");
    var historyPath = Path.Combine(repoRoot, "meta", "topics_history.json");
    var profilePath = Path.Combine(repoRoot, "meta", "author_profile.md");

    Console.WriteLine($"RepoRoot:    {repoRoot}");
    Console.WriteLine($"PostsDir:    {postsDir}");
    Console.WriteLine($"HistoryPath: {historyPath}");

    return new FileBlogRepository(repoRoot, postsDir, historyPath, profilePath);
});

builder.Services.AddSingleton<MermaidValidator>();

// 5. BlogWriterService: main orchestrator
builder.Services.AddSingleton<BlogWriterService>();

var host = builder.Build();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var blogWriter = host.Services.GetRequiredService<BlogWriterService>();
(BlogBot.Models.Topic Topic, BlogBot.Models.GeneratedPost Post, string? Path)? result;
try
{
    result = await blogWriter.RunAsync(cts.Token);
}
catch (Exception ex) when (ex is GeminiBillingException or GeminiQuotaException)
{
    // Account or free-tier limits, not a bug: print the explanation without a stack trace and fail the run.
    Console.Error.WriteLine(ex.Message);
    Environment.Exit(1);
    return;
}

// Hand the title and LinkedIn description to the workflow for the PR.
var githubOutput = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
if (result is { Path: not null } created && !string.IsNullOrEmpty(githubOutput))
{
    var delimiter = $"EOF_{Guid.NewGuid():N}";
    await File.AppendAllTextAsync(githubOutput,
        $"post_title={created.Topic.Title.Replace('\n', ' ')}\n" +
        $"post_path={Path.GetRelativePath(Path.GetDirectoryName(Path.GetDirectoryName(created.Path)!)!, created.Path)}\n" +
        $"post_slug={Path.GetFileNameWithoutExtension(created.Path)}\n" +
        $"post_checks={blogWriter.VerificationSummary}\n" +
        $"post_description<<{delimiter}\n{created.Post.Description}\n{delimiter}\n");
}

Console.WriteLine(result is null ? "No post generated." : "Blog generation completed.");
