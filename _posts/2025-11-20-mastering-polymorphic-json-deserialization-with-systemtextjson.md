---
layout: post
title: "Mastering Polymorphic JSON Deserialization with System.Text.Json"
date: 2025-11-20 03:20:55 +0000
categories: dotnet blog
canonical_url: "https://stackoverflow.com/questions/79823170/system-text-json-polymorphic-deserialization-not-binding-type-discriminator-prop"
---

When designing event-driven systems, command buses, or webhook receivers, we often find ourselves wrestling with a common challenge: flexible data contracts. The payload arriving over the wire might represent various distinct types, all sharing a common base, and our application needs to deserialize it into the *correct* concrete type. This is the essence of polymorphic deserialization, and while `System.Text.Json` has evolved, mastering its capabilities for these scenarios is critical for building robust and evolvable APIs.

I've seen countless systems stumble here, either by resorting to brittle manual parsing, complex if-else cascades, or, worse, inefficient serializations that bloat payloads. The modern .NET ecosystem, particularly with `System.Text.Json` as the default serializer, demands a more elegant and performant solution. With the prevalence of microservices, cloud-native deployments, and event sourcing, the ability to gracefully handle polymorphic data isn't just a nice-to-have; it's a foundational requirement for systems that need to adapt and scale without constant re-deploys.

### The Problem: When a `BaseType` Isn't Enough

Imagine an incoming stream of `DomainEvent` objects. We know they all derive from a common `DomainEvent` base class, but specifically, they could be `OrderCreatedEvent`, `ProductStockUpdatedEvent`, `CustomerAccountDeletedEvent`, and so on. Each concrete event type has its own distinct properties beyond what the base class provides.

```csharp
public abstract class DomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string EventType => GetType().Name; // A common, simple discriminator
}

public class OrderCreatedEvent : DomainEvent
{
    public Guid OrderId { get; init; }
    public Guid CustomerId { get; init; }
    public decimal TotalAmount { get; init; }
    public List<OrderItem> Items { get; init; } = new();
}

public class ProductStockUpdatedEvent : DomainEvent
{
    public Guid ProductId { get; init; }
    public int NewStockLevel { get; init; }
    public int OldStockLevel { get; init; }
}
// ... other event types
```

When `System.Text.Json` attempts to deserialize a JSON payload into a `DomainEvent` (or a `List<DomainEvent>`), it only has the base type information. It doesn't know which concrete derived type to instantiate. Without explicit guidance, it will instantiate `DomainEvent` itself (if it's not abstract) or throw an exception, leading to data loss or deserialization failures because properties specific to `OrderCreatedEvent` (like `OrderId` or `Items`) won't bind.

### `System.Text.Json`'s Evolving Solution: Attributes and Converters

Initially, `System.Text.Json` lagged `Newtonsoft.Json` in out-of-the-box polymorphic support. This gap has largely closed with the introduction of `JsonDerivedType` and `JsonPolymorphic` attributes in .NET 7+. These attributes offer a declarative way to tell the serializer about your type hierarchy at compile time.

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.Fail)]
[JsonDerivedType(typeof(OrderCreatedEvent), typeDiscriminator: "OrderCreated")]
[JsonDerivedType(typeof(ProductStockUpdatedEvent), typeDiscriminator: "ProductStockUpdated")]
public abstract class DomainEvent
{
    // ... base properties
}
```

This is excellent for internal APIs or tightly coupled systems where the entire hierarchy is known and controlled. The `"$type"` discriminator will be added to the JSON, and `System.Text.Json` uses it to determine the concrete type. However, for external systems, evolving APIs, or complex, non-trivial discrimination logic (e.g., based on multiple properties, external lookups, or versioning), a custom `JsonConverter` remains the most flexible and robust approach. And, frankly, for systems that aim for long-term stability and evolvability, I often prefer the explicit control a custom converter offers, even when attributes might suffice. It centralizes the logic and makes it easier to debug and extend.

### Crafting a Robust Custom Converter

Let's implement a custom `JsonConverter<DomainEvent>` to handle our event stream scenario. This converter will read a discriminator property (e.g., `EventType`) from the incoming JSON, then delegate to the standard `JsonSerializer` to deserialize the *entire object* into the correct concrete type.

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes; // For System.Text.Json.Nodes.JsonObject, often helpful

// --- Domain Event Definitions ---
public abstract class DomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    // Note: EventType property is usually derived from class name in real systems
    // but here we might have it as part of the JSON for explicit discrimination.
}

public class OrderCreatedEvent : DomainEvent
{
    public Guid OrderId { get; init; }
    public Guid CustomerId { get; init; }
    public decimal TotalAmount { get; init; }
    public List<OrderItem> Items { get; init; } = new();
}

public class OrderItem
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
}

public class ProductStockUpdatedEvent : DomainEvent
{
    public Guid ProductId { get; init; }
    public int NewStockLevel { get; init; }
    public int OldStockLevel { get; init; }
}

public class CustomerAccountDeletedEvent : DomainEvent
{
    public Guid CustomerId { get; init; }
    public string Reason { get; init; } = string.Empty;
}

// --- Custom Polymorphic Converter ---
public class DomainEventConverter : JsonConverter<DomainEvent>
{
    private const string EventTypePropertyName = "EventType"; // The discriminator property name in JSON

    public override DomainEvent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected StartObject token.");
        }

        // Create a JsonObject to buffer the entire event payload.
        // This allows us to read the discriminator property without advancing the main reader past it,
        // so we can re-parse the whole object later.
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (!root.TryGetProperty(EventTypePropertyName, out JsonElement eventTypeElement))
        {
            throw new JsonException($"Missing '{EventTypePropertyName}' discriminator property.");
        }

        var eventType = eventTypeElement.GetString();
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new JsonException($"'{EventTypePropertyName}' discriminator property cannot be empty.");
        }

        // Critical: Create new options to avoid infinite recursion.
        // The new options must NOT include this converter again.
        var newOptions = new JsonSerializerOptions(options); // Copy existing options
        newOptions.Converters.Remove(this); // Remove *this* specific converter instance

        DomainEvent? concreteEvent = eventType switch
        {
            nameof(OrderCreatedEvent) => root.Deserialize<OrderCreatedEvent>(newOptions),
            nameof(ProductStockUpdatedEvent) => root.Deserialize<ProductStockUpdatedEvent>(newOptions),
            nameof(CustomerAccountDeletedEvent) => root.Deserialize<CustomerAccountDeletedEvent>(newOptions),
            _ => throw new JsonException($"Unknown event type: {eventType}")
        };

        if (concreteEvent == null)
        {
            throw new JsonException($"Failed to deserialize event of type {eventType}.");
        }

        return concreteEvent;
    }

    public override void Write(Utf8JsonWriter writer, DomainEvent value, JsonSerializerOptions options)
    {
        // For writing, we can simply serialize the concrete type.
        // The default serializer will add its specific properties.
        // We'll also explicitly add the EventType discriminator property.

        writer.WriteStartObject();

        // Write the discriminator property first
        writer.WriteString(EventTypePropertyName, value.GetType().Name);

        // Serialize the rest of the object.
        // Again, use new options to prevent infinite recursion if `options` contains this converter.
        var newOptions = new JsonSerializerOptions(options);
        newOptions.Converters.Remove(this);

        using (JsonDocument document = JsonSerializer.SerializeToDocument(value, value.GetType(), newOptions))
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // Avoid re-writing the discriminator if the underlying type already had it.
                // This assumes `EventType` is not part of the base type's JSON representation
                // or we want our explicit one to take precedence.
                if (property.NameEquals(EventTypePropertyName)) continue;
                property.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
    }
}

// --- Integration into a Minimal API (or any .NET application) ---
// This would typically be in Program.cs for a web application.
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Configure System.Text.Json for the application.
        // Add the custom converter to the list of converters.
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new DomainEventConverter());
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.WriteIndented = true; // For readability in example
        });

        var app = builder.Build();

        app.UseHttpsRedirection();

        // Minimal API endpoint to receive and process events
        app.MapPost("/events", async (DomainEvent eventPayload, ILogger<Program> logger) =>
        {
            // The eventPayload is already deserialized into the correct concrete type
            // thanks to our custom converter.
            logger.LogInformation("Received {EventType} event: {@EventPayload}", eventPayload.GetType().Name, eventPayload);

            switch (eventPayload)
            {
                case OrderCreatedEvent oce:
                    logger.LogInformation("Processing new order {OrderId} for customer {CustomerId}", oce.OrderId, oce.CustomerId);
                    // Business logic for OrderCreatedEvent
                    break;
                case ProductStockUpdatedEvent psue:
                    logger.LogInformation("Updating stock for product {ProductId} from {OldStock} to {NewStock}", psue.ProductId, psue.OldStockLevel, psue.NewStockLevel);
                    // Business logic for ProductStockUpdatedEvent
                    break;
                case CustomerAccountDeletedEvent cade:
                    logger.LogInformation("Customer account {CustomerId} deleted for reason: {Reason}", cade.CustomerId, cade.Reason);
                    // Business logic for CustomerAccountDeletedEvent
                    break;
                default:
                    logger.LogWarning("Unhandled event type: {EventType}", eventPayload.GetType().Name);
                    break;
            }

            return Results.Ok($"Event '{eventPayload.GetType().Name}' with ID '{eventPayload.EventId}' processed.");
        })
        .WithName("ReceiveDomainEvent")
        .WithOpenApi(); // For Swagger/OpenAPI documentation

        app.Run();
    }
}
```

### Deconstructing the Converter

This `DomainEventConverter` showcases several critical patterns:

1.  **Reading the Discriminator**: In the `Read` method, we use `JsonDocument.ParseValue(ref reader)` to load the entire JSON payload into a `JsonDocument`. This is crucial because the `Utf8JsonReader` is forward-only. By parsing it into a `JsonDocument`, we can then freely navigate its `RootElement` to find our `EventType` discriminator without consuming the reader's state for the rest of the object.
2.  **Delegating Deserialization**: Once the `EventType` is identified, a `switch` statement (or a dictionary lookup for more types) maps it to the corresponding concrete type (`OrderCreatedEvent`, `ProductStockUpdatedEvent`, etc.). We then call `root.Deserialize<T>(newOptions)` to let `System.Text.Json` handle the actual deserialization of the *full JSON payload* into that specific concrete type.
3.  **Preventing Infinite Recursion**: The most common pitfall in custom `JsonConverter` implementations is infinite recursion. If the `JsonSerializer.Deserialize` call inside `Read` or `JsonSerializer.SerializeToDocument` call inside `Write` uses the *same* `JsonSerializerOptions` instance that *contains* this `DomainEventConverter`, it will try to use this converter again for the derived types, leading to a stack overflow. The solution is to create a *new* `JsonSerializerOptions` instance, copy the relevant settings from the original, and *critically* remove the current converter from its `Converters` list.
4.  **Writing Logic**: The `Write` method demonstrates how to ensure the discriminator property (`EventType`) is included in the serialized JSON. We explicitly write it first, then delegate the rest of the object's serialization. We enumerate the properties of the serialized `JsonDocument` to avoid re-writing the discriminator if the concrete type itself had it.

### Why this approach matters

*   **Flexibility and Evolutability**: New `DomainEvent` types can be added by simply extending the `switch` statement in the converter. No need to change existing consumers or attributes on base types. For very large hierarchies, consider a `Dictionary<string, Type>` mapping event type strings to `Type` instances for performance and maintainability.
*   **Explicit Control**: You dictate exactly how types are identified and handled. This is invaluable when dealing with external systems, legacy data, or complex versioning strategies.
*   **Error Handling**: The converter provides a central point for robust error handling. Missing discriminators, unknown types, or malformed JSON can be caught and logged precisely, preventing silent data loss.
*   **Performance**: While buffering with `JsonDocument` adds a slight overhead compared to purely streaming with `Utf8JsonReader`, for typical event payloads, the performance impact is negligible. `System.Text.Json` itself is highly optimized. If extreme low-latency and zero-allocation parsing are critical, a more complex, entirely manual `Utf8JsonReader`-based converter might be considered, but the complexity skyrockets. For 99% of production scenarios, `JsonDocument` is fine.

### Pitfalls and Best Practices

*   **Overly Complex Discriminators**: Keep your discriminator simple and consistent. A single string property is usually sufficient. Avoid multi-property discrimination if possible, as it significantly complicates converter logic.
*   **Discriminator Naming**: Be explicit. Using `"$type"` or `"$event"` makes it clear it's metadata, not a business property.
*   **Unknown Types Handling**: Decide how to handle unknown types. Throwing an exception (as in the example) is safe and explicit. For more resilient systems, you might deserialize into an `UnknownEvent` type with a `JsonElement` property to capture the raw payload, allowing for "dead-lettering" or forward-compatibility.
*   **Performance Hotspots**: While `JsonDocument` is fast, if you're processing millions of events per second on a single thread, profiling might reveal the buffering as a bottleneck. Only then consider a `Utf8JsonReader`-only approach, but prepare for increased code complexity.
*   **Testing**: Custom converters are business logic. They need thorough unit tests to ensure they correctly handle valid and invalid JSON, known and unknown types, and edge cases.

Ultimately, mastering polymorphic JSON deserialization with `System.Text.Json` isn't about memorizing attributes or specific API calls; it's about understanding the core challenge of type resolution at runtime and knowing when to leverage `System.Text.Json`'s declarative features versus when to take explicit control with a custom `JsonConverter`. This capability is fundamental to building flexible, resilient, and evolvable .NET applications in today's dynamic software landscape.
