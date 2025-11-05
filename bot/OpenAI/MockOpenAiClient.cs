using System.Text;
using BlogBot.Models;

namespace BlogBot.OpenAI;

public sealed class MockOpenAiClient : IOpenAIClient
{
    public Task<List<Topic>> ClusterTopicsAsync(
        List<TopicCandidate> candidates,
        CancellationToken ct = default)
    {
        var topics = candidates
            .OrderByDescending(c => c.Popularity)
            .Take(10)
            .Select((c, index) => new Topic
            {
                Id = $"topic-{index + 1}",
                Title = c.Title,
                Description = $"Mock description for '{c.Title}' from {c.Source}.",
                PrimaryUrl = c.Url,
                SupportingUrls = new List<string> { c.Url },
                AggregateScore = c.Popularity
            })
            .ToList();

        return Task.FromResult(topics);
    }

    public Task<string> GenerateBlogPostAsync(Topic topic, CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        // Title
        sb.AppendLine($"# {topic.Title}");
        sb.AppendLine();

        // Intro
        sb.AppendLine($"_Mock article generated for topic id `{topic.Id}`._");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(topic.Description))
        {
            sb.AppendLine(topic.Description);
            sb.AppendLine();
        }

        // Context section
        sb.AppendLine("## Why this matters now");
        sb.AppendLine();
        sb.AppendLine($"This is a mock article for **{topic.Title}**.");
        sb.AppendLine("In a real integration, GPT-5 would describe why this is trending for .NET developers,");
        sb.AppendLine("using recent community activity from sources like StackOverflow, Dev.to, or Reddit.");
        sb.AppendLine();

        // Source section
        if (!string.IsNullOrWhiteSpace(topic.PrimaryUrl))
        {
            sb.AppendLine("## Source material");
            sb.AppendLine();
            sb.AppendLine($"- Primary source: {topic.PrimaryUrl}");

            if (topic.SupportingUrls is { Count: > 1 })
            {
                foreach (var url in topic.SupportingUrls.Skip(1))
                {
                    sb.AppendLine($"- Supporting: {url}");
                }
            }

            sb.AppendLine();
        }

        // Example code
        sb.AppendLine("## Example in C#");
        sb.AppendLine();
        sb.AppendLine("```csharp");
        sb.AppendLine("public class Example");
        sb.AppendLine("{");
        sb.AppendLine("    public async Task RunAsync()");
        sb.AppendLine("    {");
        sb.AppendLine("        // Imagine this is some new .NET API usage related to the topic.");
        sb.AppendLine("        await Task.Delay(100);");
        sb.AppendLine($"        Console.WriteLine(\"Hello from {EscapeForStringLiteral(topic.Title)}!\");");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();

        // Explanation
        sb.AppendLine("### Explanation");
        sb.AppendLine();
        sb.AppendLine("In the real GPT-5 integration, this section would explain the important lines of the code sample,");
        sb.AppendLine("how it relates to the topic, and mention any .NET version or language-feature requirements.");
        sb.AppendLine();

        // Pitfalls
        sb.AppendLine("## Common pitfalls");
        sb.AppendLine();
        sb.AppendLine("- This is mock content. In production, GPT-5 would list real pitfalls and mistakes developers make.");
        sb.AppendLine("- The code sample is intentionally simple and should not be treated as a complete solution.");
        sb.AppendLine();

        // Best practices
        sb.AppendLine("## Best practices");
        sb.AppendLine();
        sb.AppendLine("- Apply modern .NET patterns (async/await, DI, configuration).");
        sb.AppendLine("- Validate inputs and handle exceptions properly.");
        sb.AppendLine("- Use logging and diagnostics for observability.");
        sb.AppendLine();

        // Checklist
        sb.AppendLine("## Checklist");
        sb.AppendLine();
        sb.AppendLine($"- [ ] Understand the basic scenario behind **{topic.Title}**");
        sb.AppendLine("- [ ] Try a similar code sample in your own project");
        sb.AppendLine("- [ ] Look up official .NET documentation for the APIs involved");
        sb.AppendLine("- [ ] Replace this mock generator with a real GPT-5 integration");

        return Task.FromResult(sb.ToString());
    }

    private static string EscapeForStringLiteral(string value)
    {
        // Only needed because we embed the title into a C# string literal inside the Markdown code.
        return value.Replace("\"", "\\\"");
    }
}
