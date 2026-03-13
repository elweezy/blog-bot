---
layout: post
title: "Modern C# Type System: Choosing the Right Type (Struct, Record, Class)"
date: 2026-03-11 03:50:37 +0000
categories: dotnet blog
canonical_url: "https://dev.to/cristiansifuentes/struct-vs-record-vs-class-choosing-the-right-type-in-modern-c-net-910-edition-5ea2"
---

The mental model we apply to data in our systems shapes everything, from performance profiles to debugging nightmares. For years, the `class` was the default, often the only real choice for complex data. Then `struct`s lingered in the shadows, mostly for primitive types or niche performance optimizations. Modern C# has dramatically expanded our toolkit, particularly with `record` types. The question is no longer "class or struct?", but rather, "which of these finely-tuned instruments is right for the job?"

This isn't about arbitrary preference; it's about semantic intent, performance characteristics, and long-term maintainability. With the current stable .NET runtime and C# features, the nuances between `class`, `record class`, `struct`, and `record struct` are more pronounced and more impactful than ever before. Cloud-native architectures, with their emphasis on immutability, functional patterns, and efficient data transfer, make this decision a front-and-center architectural consideration, not an afterthought.

### The Foundation: Identity vs. Value Semantics

At the core of this decision lies a fundamental philosophical distinction: Does this piece of data represent a unique *identity* that can be referenced and potentially mutated, or does it represent a *value* whose meaning is derived solely from its content, and which is interchangeable with any other piece of data containing the same content?

#### `class`: The Reference Identity

The venerable `class` type remains the workhorse of object-oriented programming in C#. It's a reference type, meaning variables hold references to objects on the heap, and two variables can reference the *same* object.

*   **Identity:** A `class` instance has a unique identity in memory. `==` by default checks for reference equality. This is crucial for entities where identity matters, like a `User` in a database, a `Service` instance in a DI container, or any object whose lifecycle or side-effects are tied to its specific instance.
*   **Mutability:** `class` instances are mutable by default. While you can enforce immutability with `readonly` fields and `init` properties, it's an opt-in discipline.
*   **Inheritance & Polymorphism:** `class` types fully support inheritance and polymorphism, making them ideal for complex object hierarchies.
*   **Memory:** Allocated on the managed heap, subject to garbage collection. References consume 8 bytes (on 64-bit systems). Passing a class instance only copies the reference, not the entire object.

Use a `class` when: identity is important, you need inheritance, or the object inherently manages complex mutable state that is shared.

#### `struct`: The Value Copy

`struct` types are value types. Unlike `class`es, a variable of a `struct` type directly contains its data. When assigned or passed as an argument, the *entire struct is copied*.

*   **Value Semantics:** `==` by default performs a member-wise comparison. Two `struct`s are equal if their fields are equal, regardless of memory location.
*   **Immutability (Crucial!):** **Always design `struct`s to be immutable.** A mutable `struct` is a source of subtle bugs, as modifying a copy won't affect the original. Use `readonly struct` to enforce this at compile time.
*   **Memory:** Typically allocated on the stack (for local variables) or inline within another object (if it's a field of a class or another struct). This can reduce heap allocations and GC pressure, improving cache locality. However, large `struct`s (>16-32 bytes, a rule of thumb) can be detrimental, as copying them becomes expensive. Boxing (converting a value type to an object) can also negate performance benefits and introduce heap allocations.
*   **No Inheritance:** `struct`s cannot inherit from other types (besides `System.ValueType`).

Use a `readonly struct` for small, immutable value types where identity is irrelevant, and copying them is inexpensive (e.g., `Point`, `Guid`, `Money`, `RgbColor`). They are perfect for truly independent "values."

#### `record`: The Immutable Data Carrier

`record` types, introduced in C# 9, are a powerful abstraction for data-centric types. They come in two flavors: `record class` (reference type) and `record struct` (value type, C# 10+). Their primary purpose is to simplify the creation of immutable types with value equality semantics.

*   **Immutability by Default:** `record` properties are `init`-only by default, encouraging immutable patterns.
*   **Value Equality:** `record` types automatically implement value-based equality (`Equals`, `GetHashCode`, `operator ==`) based on the values of their properties/fields. This is a significant boon for DTOs, messages, and domain value objects.
*   **Non-Destructive Mutation:** The `with` expression allows creating a new instance of a `record` with specific properties changed, leaving the original instance untouched. This is a cornerstone of functional programming patterns.
*   **Concise Syntax:** Positional records generate constructors, properties, and `ToString()` automatically, significantly reducing boilerplate.

**`record class` (Reference Type):**
Behaves like a `class` under the hood regarding memory allocation (heap) and reference variables, but with `record`'s value equality, immutability, and `with` expression features. Ideal for:
*   Data Transfer Objects (DTOs)
*   Messages in messaging queues
*   Immutable domain value objects (e.g., `Address`, `ProductId`)
*   Configuration objects

**`record struct` (Value Type, C# 10+):**
Combines the benefits of `record`s (value equality, `with` expression, concise syntax) with the benefits of `struct`s (value semantics, potential stack allocation, no GC pressure).
*   **Crucial:** Like `struct`s, `record struct`s should always be `readonly` and small. Use `readonly record struct` to enforce immutability.
*   Good for scenarios where a `struct` is appropriate, but you also want the niceties of `record`s, especially generated value equality and the `with` expression.

### Architecting with Precision: A Practical Example

Consider a scenario in a modern ASP.NET Core application, perhaps a minimal API, where we manage user preferences. We need to define data structures for configuration, user preferences, and a custom value type.

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent; // Using ConcurrentDictionary for thread-safety in demo
using System.Threading.Tasks;

// 1. A readonly record struct for a small, immutable value type.
// Represents an RGB color. It's a fundamental value, identity doesn't matter, it's small.
// Using 'record struct' provides value equality and a concise declaration.
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    // Custom ToString for better readability in API responses/logs
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

// 2. A record class for immutable data transfer objects (DTOs) and configuration.
// UserPreferences: Identity is less about the object's memory location and more about its UserId and content.
// Value equality is useful for comparing preference sets. Immutability prevents accidental changes.
public record UserPreferences(
    Guid UserId,
    RgbColor ThemePrimaryColor,
    bool EmailNotificationsEnabled,
    DateTime LastUpdatedUtc
);

// AppConfiguration: Immutable configuration bound from settings.
// A record class ensures it's treated as a value and easily compared or passed around.
public record AppConfiguration(string DefaultThemeColorHex, int MaxNotificationRetries);

// 3. A traditional class for a service layer.
// UserPreferencesService: This service has identity. It manages state (even if in-memory for this demo)
// and provides behavior. It will be dependency-injected.
public class UserPreferencesService
{
    private readonly ILogger<UserPreferencesService> _logger;
    private readonly AppConfiguration _config;
    // Using ConcurrentDictionary for thread-safety in this demo's in-memory store
    private readonly ConcurrentDictionary<Guid, UserPreferences> _preferencesStore = new();

    public UserPreferencesService(ILogger<UserPreferencesService> logger, AppConfiguration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        // Seed some data with default color from config
        var defaultColor = ParseRgbColor(_config.DefaultThemeColorHex);
        _preferencesStore.TryAdd(Guid.Parse("a1b2c3d4-e5f6-7890-1234-567890abcdef"),
            new UserPreferences(
                Guid.Parse("a1b2c3d4-e5f6-7890-1234-567890abcdef"),
                defaultColor,
                true,
                DateTime.UtcNow
            )
        );
    }

    public Task<UserPreferences?> GetPreferencesAsync(Guid userId)
    {
        _logger.LogInformation("Attempting to retrieve preferences for user {UserId}", userId);
        _preferencesStore.TryGetValue(userId, out var preferences);
        return Task.FromResult(preferences);
    }

    public Task UpdatePreferencesAsync(UserPreferences preferences)
    {
        _logger.LogInformation("Update request for user {UserId} received.", preferences.UserId);

        _preferencesStore.AddOrUpdate(
            preferences.UserId,
            // Add case: set LastUpdatedUtc on creation
            (key) => preferences with { LastUpdatedUtc = DateTime.UtcNow },
            // Update case: use 'with' expression for non-destructive mutation
            (key, existing) => existing with
            {
                ThemePrimaryColor = preferences.ThemePrimaryColor,
                EmailNotificationsEnabled = preferences.EmailNotificationsEnabled,
                LastUpdatedUtc = DateTime.UtcNow // Always stamp modification time
            }
        );
        _logger.LogInformation("Preferences for user {UserId} updated successfully.", preferences.UserId);
        return Task.CompletedTask;
    }

    private RgbColor ParseRgbColor(string hex)
    {
        // Simple parsing for demo; production would have robust error handling
        if (hex.StartsWith("#")) hex = hex[1..];
        byte r = Convert.ToByte(hex.Substring(0, 2), 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);
        return new RgbColor(r, g, b);
    }
}

// Minimal API setup
var builder = WebApplication.CreateBuilder(args);

// Configure services
// Bind AppConfiguration using a record class. IConfiguration supports this seamlessly.
builder.Services.AddSingleton(builder.Configuration.GetSection("App").Get<AppConfiguration>() 
                              ?? throw new InvalidOperationException("App configuration not found."));
builder.Services.AddSingleton<UserPreferencesService>();
builder.Logging.AddConsole(); // Add console logger

var app = builder.Build();

app.MapGet("/preferences/{userId:guid}", async (Guid userId, UserPreferencesService service) =>
{
    var preferences = await service.GetPreferencesAsync(userId);
    return preferences is not null ? Results.Ok(preferences) : Results.NotFound();
})
.WithName("GetUserPreferences")
.WithOpenApi();

app.MapPut("/preferences", async (UserPreferences preferences, UserPreferencesService service) =>
{
    await service.UpdatePreferencesAsync(preferences);
    return Results.NoContent();
})
.WithName("UpdateUserPreferences")
.WithOpenApi();

app.Run();

// Example appsettings.json:
/*
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "App": {
    "DefaultThemeColorHex": "#FF5733", // Example: a reddish-orange
    "MaxNotificationRetries": 3
  }
}
*/
```

**Reasoning Behind the Choices:**

1.  **`RgbColor` as `readonly record struct`:** An `RgbColor` is a canonical example of a value type. Its identity is defined purely by its R, G, and B components. It's small, immutable, and benefits from value semantics (copying, potential stack allocation). The `record struct` syntax provides automatic value equality comparison and a `ToString()` implementation, reducing boilerplate compared to a plain `struct`. The `readonly` modifier guarantees immutability, preventing subtle bugs caused by mutable struct copies.
2.  **`UserPreferences` as `record class`:** User preferences are data. While they are associated with a `UserId`, the `UserPreferences` object itself is best treated as an immutable snapshot of those preferences. Value equality is highly desirable here: two `UserPreferences` objects are "the same" if their content is identical, regardless of where they sit in memory. The `with` expression allows for elegant, non-destructive updates to specific fields, which aligns perfectly with modern immutable data patterns. Being a `record class` means it lives on the heap, which is fine for a DTO that might be larger and passed through various layers.
3.  **`AppConfiguration` as `record class`:** Configuration bound from `appsettings.json` is typically immutable after startup. A `record class` makes this explicit and provides the same benefits as `UserPreferences` in terms of value equality and concise definition. It's a cohesive data bag.
4.  **`UserPreferencesService` as `class`:** A service object inherently has identity. It holds dependencies (`ILogger`, `AppConfiguration`), manages mutable state (`_preferencesStore`), and orchestrates operations. It's a singleton (in this demo) injected into other components. Reference semantics are entirely appropriate here. We expect *one* instance of `UserPreferencesService` to manage preferences.

### Pitfalls and Best Practices

The modern C# type system empowers us, but with power comes responsibility.

*   **Avoid Large `struct`s:** The hard rule for `struct`s is that they must be *small*. While "small" is subjective, generally if a `struct` is larger than 16-32 bytes (e.g., more than 2-4 fields of `int` or `long`), the cost of copying it around can outweigh any benefits of reduced GC pressure. Large `struct`s often lead to hidden performance penalties.
*   **Embrace `readonly` for `struct`s:** If you use a `struct` (or `record struct`), make it `readonly`. Mutable `struct`s are a common source of difficult-to-diagnose bugs because copies are made implicitly, leading to modifications on a copy, not the original. `readonly struct` forces immutability and ensures predictable behavior.
*   **Don't Over-Record Entities with Strong Identity:** While `record class` is fantastic for DTOs and value objects, be cautious using it for true domain *entities* (e.g., the primary `Customer` object managed by an ORM) where object identity (memory address) is often implicitly relied upon for tracking, caching, or equality within specific contexts. A `class` with `init` properties might still be a better choice for an entity if `Equals()` based on content rather than identity could cause confusion in an identity-driven domain.
*   **Distinguish between `init` and `readonly`:** `init` accessors allow properties to be set only during object initialization (constructor or object initializer). `readonly` fields can only be assigned in the constructor. You can have `class`es with `init` properties to get similar immutability benefits to `record class` without opting into all `record` features (like generated equality).
*   **Consider Boxing:** When a `struct` is treated as an `object` (e.g., passed to an `object` parameter, stored in a non-generic collection like `ArrayList`), it is "boxed" onto the heap. This negates the performance benefits of `struct`s and creates GC pressure. Be mindful of this in performance-critical paths.

The rich type system of modern C# offers precise tools to model your domain. The choice between `class`, `record class`, `struct`, and `record struct` is a deliberate architectural decision. It's about aligning the type's inherent semantics—identity versus value—with its role in your system, considering immutability, performance, and the clarity it brings to your codebase. Embrace these distinctions, and your code will be more robust, more maintainable, and often, significantly faster.
