---
layout: post
title: "Leveraging Incremental Source Generators for Enhanced .NET Development"
date: 2025-11-12 03:22:16 +0000
categories: dotnet blog
canonical_url: "https://dev.to/roxeem/incremental-source-generators-in-net-38gk"
---

It used to be that extending a framework or automating boilerplate meant reaching for reflection, IL weaving, or — heaven forbid — T4 templates run as pre-build steps. I’ve spent countless hours debugging `MissingMethodException`s in production, tracing back to some runtime magic that didn't quite line up with a release build, or trying to explain why a build server was churning for minutes on end due to a custom code generator script. The developer experience was often… less than ideal.

Then came .NET source generators. And while the initial versions were a revelation, they still felt a bit like a blunt instrument. Regenerating *everything* on every small change could be sluggish, particularly in larger solutions. It wasn't until `IIncrementalGenerator` landed that I truly saw the paradigm shift possible. This wasn't just about moving code generation to compile-time; it was about doing it *smartly*, efficiently, and in a way that truly integrated into the IDE experience without grinding it to a halt.

### Why Incremental Generators are a Game Changer Now

In the modern .NET landscape, where performance is paramount, cold start times for cloud-native applications are scrutinized, and AOT compilation is becoming increasingly relevant, incremental source generators are an indispensable tool in an architect's arsenal. They allow us to:

1.  **Shift Runtime Overhead to Compile Time:** Eliminate reflection, manual string manipulation for dynamic calls, or even complex expression tree compilation. The code is *just there*, fully compiled and optimized by the C# compiler. This is huge for performance-critical services.
2.  **Reduce Boilerplate and Improve Developer Experience:** Think `INotifyPropertyChanged`, strongly-typed logging, dependency injection helpers, or even custom API endpoint generation. We can automate the repetitive, error-prone code, freeing developers to focus on business logic.
3.  **Enhance Code Maintainability and Safety:** Generated code is part of the compilation, meaning it's type-checked and discoverable by the IDE. No more magic strings or untyped dictionaries hiding runtime errors.
4.  **Boost AOT Compatibility:** By generating concrete code instead of relying on runtime reflection, generators naturally produce AOT-friendly outputs, helping applications achieve smaller sizes and faster startup times.
5.  **Maintain IDE Responsiveness:** The "incremental" part is key. The C# compiler only re-evaluates parts of the generation pipeline affected by a change, making the developer inner loop much faster than with non-incremental generators.

It's not just about writing less code; it's about writing *better* code by leveraging the compiler to do the heavy lifting, ensuring consistency and correctness across your codebase.

### Deep Dive: The Incremental Pipeline

At its core, an incremental source generator works by building a *pipeline* of transformations on syntax and semantic information. Instead of giving you a complete snapshot of the compilation and asking you to figure out what changed, `IIncrementalGenerator` provides a reactive, LINQ-like API to declare how inputs should be processed.

The `Initialize` method is where you set up this pipeline. You get an `IncrementalGeneratorInitializationContext` which exposes `SyntaxProvider`, `AnalyzerConfigOptionsProvider`, and other sources of data. The magic happens with `SyntaxProvider.CreateSyntaxProvider`, which allows you to define how to identify relevant syntax nodes and then transform them.

Key concepts in play:

*   **`IIncrementalGenerator`:** The interface your generator class implements.
*   **`GeneratorInitializationContext`:** The entry point for defining your pipeline.
*   **`IncrementalValuesProvider<T>`:** Represents a stream of values that can be incrementally updated. Think of it like an `IObservable<T>` for compilation data.
*   **`SyntaxProvider.CreateSyntaxProvider(...)`:** This is your primary mechanism to find syntax nodes. You provide two functions:
    *   `predicate`: A fast filter (e.g., `(syntaxNode, CancellationToken) => syntaxNode is ClassDeclarationSyntax`) to quickly narrow down potential candidates. This should be purely syntactic and avoid allocating.
    *   `transform`: A method to convert the filtered `SyntaxNode` into a meaningful `T` that includes semantic information (e.g., type symbol, attributes). This is where you might use `context.SemanticModel`.
*   **`Select`, `Where`, `Combine`, `Collect`:** LINQ-like methods to manipulate `IncrementalValuesProvider<T>` instances, building up your data pipeline. `Combine` is particularly powerful for joining different streams of data (e.g., class declarations with their method declarations).
*   **`RegisterSourceOutput(...)`:** The final step, where you take the output of your pipeline and emit actual C# source code.

The compiler automatically tracks dependencies between these pipeline stages. If only a single class changes, only the parts of the pipeline dependent on that class's syntax or semantic information are re-evaluated, leading to vastly improved performance.

### Practical Example: Generating Minimal API Endpoint Groups

Let's illustrate this with a realistic scenario: defining Minimal API endpoint groups in a more structured, discoverable way. Often, we scatter `MapGet`, `MapPost`, etc., calls throughout `Program.cs` or in extension methods. What if we could define an `EndpointGroup` class and have a generator automatically register all its methods as endpoints?

We'll create an attribute `[GenerateApiGroup("/api/v1/products")]` and use it on a `partial class` that defines endpoint methods. The generator will then create an extension method like `MapProductEndpoints(this WebApplication app)` that sets up the group and maps the methods.

First, the attribute definition (in a separate project, typically):

```csharp
// Common/Attributes/GenerateApiGroupAttribute.cs
using System;

namespace MyApi.Common.Attributes
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class GenerateApiGroupAttribute : Attribute
    {
        public string RoutePrefix { get; }

        public GenerateApiGroupAttribute(string routePrefix)
        {
            if (string.IsNullOrWhiteSpace(routePrefix))
            {
                throw new ArgumentException("Route prefix cannot be null or whitespace.", nameof(routePrefix));
            }
            RoutePrefix = routePrefix;
        }

        public string GroupName { get; set; } // Optional: for group.WithGroupName()
    }
}
```

Now, how an API developer would use it:

```csharp
// MyApi/Endpoints/ProductEndpoints.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc; // For common attributes
using System.Threading.Tasks;
using MyApi.Common.Attributes; // The attribute we defined

namespace MyApi.Endpoints
{
    // The generator will create a method like `public static WebApplication MapProductEndpoints(this WebApplication app)`
    // in an extension class, which will map the routes defined here.
    [GenerateApiGroup("/api/v1/products", GroupName = "Product Management")]
    public static partial class ProductEndpoints // Must be partial!
    {
        [HttpGet("/")]
        public static IResult GetAllProducts()
        {
            return Results.Ok(new[] { "Product A", "Product B" });
        }

        [HttpGet("/{id:guid}")]
        public static IResult GetProductById([FromRoute] Guid id)
        {
            // Simulate fetching a product
            if (id == Guid.Empty)
            {
                return Results.NotFound();
            }
            return Results.Ok($"Product {id}");
        }

        [HttpPost("/")]
        public static async Task<IResult> CreateProduct([FromBody] ProductCreateModel model)
        {
            await Task.Delay(100); // Simulate async operation
            return Results.Created($"/api/v1/products/{Guid.NewGuid()}", model.Name);
        }
    }

    public record ProductCreateModel(string Name, decimal Price);
}

// In Program.cs:
// app.MapProductEndpoints(); // This call comes from generated code!
```

Finally, the incremental source generator itself:

```csharp
// MyApi.Generator/ApiGroupGenerator.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using MyApi.Common.Attributes; // Reference your attribute project

namespace MyApi.Generator
{
    [Generator]
    public class ApiGroupGenerator : IIncrementalGenerator
    {
        private const string GenerateApiGroupAttributeFullName = "MyApi.Common.Attributes.GenerateApiGroupAttribute";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // 1. Find all classes decorated with GenerateApiGroupAttribute
            //    We use ForAttributeWithMetadata for efficient attribute-based filtering and semantic data.
            IncrementalValuesProvider<ApiGroupDefinition> apiGroups = context.SyntaxProvider
                .ForAttributeWithMetadata<GenerateApiGroupAttribute>(
                    predicate: static (syntaxNode, _) => syntaxNode is ClassDeclarationSyntax, // Quickly filter for class declarations
                    transform: static (syntaxContext, cancellationToken) => // Transform into a structured definition
                    {
                        if (syntaxContext.TargetSymbol is not INamedTypeSymbol classSymbol)
                        {
                            return null; // Should not happen given predicate
                        }

                        // Get the attribute data
                        AttributeData? attribute = classSymbol.GetAttributes()
                            .FirstOrDefault(ad => ad.AttributeClass?.ToDisplayString() == GenerateApiGroupAttributeFullName);

                        if (attribute == null) return null; // Should not happen

                        // Extract route prefix from constructor argument
                        string? routePrefix = attribute.ConstructorArguments.FirstOrDefault().Value?.ToString();
                        if (string.IsNullOrWhiteSpace(routePrefix)) return null;

                        // Extract optional GroupName property
                        string? groupName = attribute.NamedArguments
                            .FirstOrDefault(na => na.Key == nameof(GenerateApiGroupAttribute.GroupName)).Value.Value?.ToString();

                        // Collect methods within this class that could be API endpoints
                        List<ApiMethodDefinition> methods = new();
                        foreach (var member in classSymbol.GetMembers())
                        {
                            if (member is IMethodSymbol methodSymbol && !methodSymbol.IsStatic && methodSymbol.DeclaredAccessibility == Accessibility.Public)
                            {
                                // Check for common HTTP method attributes (HttpGet, HttpPost, etc. from ASP.NET Core MVC/Minimal APIs)
                                // This is simplified; in a real scenario, you'd check for specific attribute types or interfaces.
                                var httpAttribute = methodSymbol.GetAttributes()
                                    .FirstOrDefault(ad => ad.AttributeClass?.ToDisplayString().StartsWith("Microsoft.AspNetCore.Mvc.HttpGet") == true ||
                                                          ad.AttributeClass?.ToDisplayString().StartsWith("Microsoft.AspNetCore.Mvc.HttpPost") == true ||
                                                          ad.AttributeClass?.ToDisplayString().StartsWith("Microsoft.AspNetCore.Mvc.HttpPut") == true ||
                                                          ad.AttributeClass?.ToDisplayString().StartsWith("Microsoft.AspNetCore.Mvc.HttpDelete") == true);

                                if (httpAttribute != null)
                                {
                                    string httpMethod = httpAttribute.AttributeClass?.Name.Replace("Attribute", "") ?? "GET"; // e.g., "HttpGet" -> "Get"
                                    string? route = httpAttribute.ConstructorArguments.FirstOrDefault().Value?.ToString();

                                    methods.Add(new ApiMethodDefinition(httpMethod, route, methodSymbol.Name));
                                }
                            }
                        }

                        return new ApiGroupDefinition(
                            classSymbol.ContainingNamespace?.ToDisplayString() ?? "global",
                            classSymbol.Name,
                            routePrefix,
                            groupName,
                            methods
                        );
                    }
                )
                .Where(static d => d != null)!; // Filter out nulls from transform, if any

            // 2. Register the source output for each discovered API group
            context.RegisterSourceOutput(apiGroups.Collect(), static (context, groups) =>
            {
                if (groups.IsDefaultOrEmpty) return;

                var sb = new StringBuilder();
                sb.AppendLine("// <auto-generated/>");
                sb.AppendLine("#nullable enable");
                sb.AppendLine("using Microsoft.AspNetCore.Builder;");
                sb.AppendLine("using Microsoft.AspNetCore.Routing;"); // For MapGroup
                sb.AppendLine("using Microsoft.Extensions.DependencyInjection;"); // For IServiceCollection, if needed for other generators
                sb.AppendLine("using System;"); // For Guid, if generated routes use it

                sb.AppendLine();
                sb.AppendLine("namespace MyApi.Generated"); // Or a more specific namespace
                sb.AppendLine("{");
                sb.AppendLine("    public static partial class EndpointExtensions"); // Partial for extensibility
                sb.AppendLine("    {");

                foreach (var groupDef in groups.Distinct()) // Ensure unique groups, though unlikely with class-level attribute
                {
                    GenerateApiGroupExtensionMethod(sb, groupDef);
                }

                sb.AppendLine("    }");
                sb.AppendLine("}");

                context.AddSource("ApiGroupExtensions.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
            });
        }

        private static void GenerateApiGroupExtensionMethod(StringBuilder sb, ApiGroupDefinition groupDef)
        {
            var mapMethodName = $"Map{groupDef.ClassName}Endpoints"; // e.g., MapProductEndpoints
            var groupVarName = $"{char.ToLowerInvariant(groupDef.ClassName[0])}{groupDef.ClassName.Substring(1)}Group"; // e.g., productGroup

            sb.AppendLine($"        public static WebApplication {mapMethodName}(this WebApplication app)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var {groupVarName} = app.MapGroup(\"{groupDef.RoutePrefix}\");");

            if (!string.IsNullOrWhiteSpace(groupDef.GroupName))
            {
                sb.AppendLine($"            {groupVarName}.WithGroupName(\"{groupDef.GroupName}\");");
            }

            foreach (var method in groupDef.Methods)
            {
                // This assumes method.HttpMethod is something like "Get", "Post", "Put", "Delete"
                // And method.Route is the path relative to the group prefix
                sb.AppendLine($"            {groupVarName}.Map{method.HttpMethod}(\"{method.Route}\", {groupDef.Namespace}.{groupDef.ClassName}.{method.MethodName});");
            }
            sb.AppendLine($"            return app;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        // Internal record types for pipeline data, enabling efficient caching and comparison
        private record ApiGroupDefinition(
            string Namespace,
            string ClassName,
            string RoutePrefix,
            string? GroupName,
            IReadOnlyList<ApiMethodDefinition> Methods
        );

        private record ApiMethodDefinition(
            string HttpMethod,
            string? Route, // Can be null for root path
            string MethodName
        );
    }
}
```

#### Explaining the Generator Code

1.  **`Initialize` Method:** This is the entry point.
2.  **`context.SyntaxProvider.ForAttributeWithMetadata`:** This is the *most crucial* part for attribute-driven generators. It intelligently finds all types decorated with `GenerateApiGroupAttribute`.
    *   The `predicate` (`static (syntaxNode, _) => syntaxNode is ClassDeclarationSyntax`) acts as a first, cheap filter. The compiler uses this to quickly identify potential candidates without doing expensive semantic analysis.
    *   The `transform` (`static (syntaxContext, cancellationToken) => { ... }`) takes the filtered syntax node and its associated semantic model (`GeneratorAttributeSyntaxContext`) and extracts all necessary information: the `RoutePrefix`, `GroupName`, and details about the methods marked as HTTP endpoints.
    *   I'm creating lightweight `record` types (`ApiGroupDefinition`, `ApiMethodDefinition`) to hold this extracted data. Records inherently provide value equality, which is vital for the incremental pipeline to determine if a value has *actually* changed and thus needs re-processing.
3.  **Method Discovery:** Inside the `transform`, I iterate through the class members (`classSymbol.GetMembers()`) and look for public, non-static methods. I then check for common ASP.NET Core HTTP attributes (like `[HttpGet]`, `[HttpPost]`). In a production generator, you'd likely make this more robust, perhaps by defining your *own* HTTP method attributes or looking for specific interfaces if the methods were instance methods.
4.  **`apiGroups.Collect()`:** This gathers all the individual `ApiGroupDefinition`s into a single `ImmutableArray<ApiGroupDefinition>`. The `RegisterSourceOutput` callback will then receive this collection.
5.  **`context.AddSource(...)`:** This is where the actual C# code is emitted. I'm building a `StringBuilder` to construct the `EndpointExtensions` class and its `MapXyzEndpoints` methods.
    *   The generated extension method (`MapProductEndpoints`) correctly uses `app.MapGroup()` and then maps each discovered method to its corresponding HTTP verb and route.
    *   Notice the `partial class` definition for `EndpointExtensions`. This is a best practice, allowing other generators (or even manual code) to add members to the same extension class without conflicts.
    *   The generated file is given a descriptive name (`ApiGroupExtensions.g.cs`) and includes the `// <auto-generated/>` comment, which is a standard convention.

To use this, you'd create a new .NET Standard 2.0 or 2.1 class library project for `MyApi.Generator`, add `Microsoft.CodeAnalysis.CSharp` (and potentially `Microsoft.CodeAnalysis.Analyzers`) NuGet package, reference `MyApi.Common` (for the attribute) and then reference `MyApi.Generator` in your main `MyApi` project using `<ProjectReference Include="..\MyApi.Generator\MyApi.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />`.

### Pitfalls & Best Practices I've Learned the Hard Way

Working with source generators isn't always smooth sailing. Here are some lessons from the trenches:

*   **Don't Over-Optimize the `predicate`:** The `predicate` in `CreateSyntaxProvider` should be *fast* and purely syntactic. Don't try to access semantic information or allocate objects here. Its job is to quickly rule out irrelevant syntax nodes. If you move too much logic into the predicate, you might inadvertently make the initial filtering slower than necessary.
*   **Embrace `record` types for Pipeline Data:** As shown in the example, define `record` types for the data flowing through your pipeline. Records provide automatic value equality, which the incremental compiler uses to determine if a step needs re-execution. If you use classes without custom `Equals`/`GetHashCode`, the compiler might re-process everything unnecessarily, negating the "incremental" benefit.
*   **Keep Generated Code Minimal and Focused:** Just because you *can* generate a lot of code doesn't mean you *should*. Each generator should ideally solve one specific problem. Large, monolithic generators can become slow, hard to debug, and difficult to maintain.
*   **Debugging Generators:** This is often a pain point. My go-to method is to add `Debugger.Launch()` at the beginning of the `Initialize` method (or inside a transform if I need to debug deeper). Then, when you build your consuming project, a debugger attachment prompt appears. Another trick is to set up a `launchSettings.json` in your generator project to launch Visual Studio with a test project open, passing the necessary build commands.
*   **`ToString()` is a Trap:** Avoid relying on `SyntaxNode.ToString()` for semantic information. It's usually inefficient and often loses fidelity. Always go through the `SemanticModel` to get accurate symbol information.
*   **Test Your Generators:** Just like any critical component, generators need robust testing. You can write unit tests that feed in `SyntaxTree`s and assert on the generated `SourceText`. Integration tests in a sample project are also essential to ensure the generated code actually compiles and behaves as expected.
*   **User Experience (IDE Integration):** Remember that users will see errors and warnings from your generated code. Ensure your generator provides clear diagnostics (`context.ReportDiagnostic`) if it detects misuse of your attributes or invalid input patterns.
*   **Avoid Collisions:** Be careful with naming generated files and types. Use namespaces and partial classes effectively. Prefixes like `.g.cs` or `Generated` in namespaces are good conventions.

### The Future is Compile-Time

Incremental source generators have fundamentally changed how I approach code automation and framework extension in .NET. They bridge the gap between static analysis and dynamic runtime behavior, empowering us to build more performant, maintainable, and robust systems.

As .NET continues to evolve with features like Native AOT and an ever-increasing focus on performance, compile-time tooling like incremental source generators will only become more vital. They allow us to push complexity out of the runtime and into the build process, resulting in cleaner code, faster applications, and happier developers. If you haven't yet dipped your toes into this powerful feature, now is absolutely the time.
