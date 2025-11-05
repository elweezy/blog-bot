using BlogBot.Models;
using BlogBot.OpenAI;
using BlogBot.Repository;

namespace BlogBot.Services;

public sealed class BlogWriterService
{
    private readonly TopicDiscoveryService _discovery;
    private readonly IOpenAIClient _openAiClient;
    private readonly FileBlogRepository _blogRepo;

    public BlogWriterService(
        TopicDiscoveryService discovery,
        IOpenAIClient openAiClient,
        FileBlogRepository blogRepo)
    {
        _discovery = discovery;
        _openAiClient = openAiClient;
        _blogRepo = blogRepo;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var candidates = await _discovery.GetCandidatesAsync(ct);
        if (candidates.Count == 0)
        {
            Console.WriteLine("No topic candidates found. Exiting.");
            return;
        }

        var topics = await _openAiClient.ClusterTopicsAsync(candidates, ct);
        if (topics.Count == 0)
        {
            Console.WriteLine("OpenAI returned no topics. Exiting.");
            return;
        }

        var history = _blogRepo.LoadHistory();
        var notRecentlyUsed = topics
            .Where(t => !history.IsRecentlyUsed(t.Id, days: 30))
            .ToList();

        var pool = notRecentlyUsed.Count > 0 ? notRecentlyUsed : topics;

        var chosen = ChooseRandom(pool);
        Console.WriteLine($"Chosen topic: {chosen.Title} (Id: {chosen.Id})");

        var markdownBody = await _openAiClient.GenerateBlogPostAsync(chosen, ct);

        var postPath = _blogRepo.CreatePostFile(chosen, markdownBody);
        Console.WriteLine($"Created post: {postPath}");

        history.Add(chosen.Id);
        _blogRepo.SaveHistory(history);
    }

    private static Topic ChooseRandom(List<Topic> topics)
    {
        var rnd = new Random();
        return topics[rnd.Next(topics.Count)];
    }
}
