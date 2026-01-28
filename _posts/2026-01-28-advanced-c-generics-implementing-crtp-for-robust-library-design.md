---
layout: post
title: "Advanced C# Generics: Implementing CRTP for Robust Library Design"
date: 2026-01-28 03:37:50 +0000
categories: dotnet blog
canonical_url: "https://dev.to/reslava/why-i-chose-crtp-for-my-c-result-pattern-library-2a49"
---

When designing robust, extensible libraries in C#, we often find ourselves at a crossroads: how do we create a base abstraction that provides common functionality while simultaneously allowing derived types to introduce specific behaviors, *and* ensure that the fluent API methods on the base type always return the *most derived* type? Standard inheritance with virtual methods works well for runtime polymorphism, but it doesn't give the base class compile-time knowledge of its concrete subclass. This can lead to awkward casting, loss of specific type information in fluent chains, or reliance on runtime reflection, all of which compromise type safety and performance.

This is precisely the scenario where the Curiously Recurring Template Pattern (CRTP) shines in C#. It's a powerful generic pattern that allows a base class to be parameterized by its own derived type. While it might sound like a recursive riddle, it’s an elegant solution for achieving static polymorphism and enforcing compile-time constraints, giving library authors a robust mechanism for building highly functional and type-safe APIs.

### The Power of Static Polymorphism in Library Design

Modern .NET development increasingly emphasizes performance, type safety, and maintainability. In this context, patterns that leverage compile-time guarantees over runtime lookups are invaluable. CRTP enables static polymorphism, meaning type resolution happens at compile time, eliminating the overhead of virtual method dispatch and enabling the compiler to catch type mismatches before a single line of code executes at runtime. For core library components, fluent builders, or domain-specific language (DSL) implementations, this offers significant advantages. It's not a replacement for traditional polymorphism, but a specialized tool for when the base class needs to "know" its exact derived type to provide services or return instances of that specific type.

Consider the common `Result` pattern, which I've used extensively in mission-critical services. We want a generic `Result<TSuccess, TError>` that handles success or failure states. But sometimes, a specific operation might require a `ServiceOperationResult<TSuccess, TError>` that carries additional metadata, like a `CorrelationId` or `Timestamp`. We need fluent methods like `Then`, `Map`, or `OnFailure` on the base `Result` to operate on and *return* instances of `ServiceOperationResult`, preserving that specific derived type throughout a chain of operations. Standard inheritance typically returns the base `Result` type, forcing casts and eroding type safety. CRTP elegantly solves this by allowing the base `Result` to define its fluent API methods in terms of `TSelf`, where `TSelf` *is* the derived type.

### Implementing CRTP: The Mechanics

The core of CRTP in C# is a generic base class that takes its own derived type as a generic parameter:

```csharp
public abstract class Base<TSelf> where TSelf : Base<TSelf>
{
    // Methods here can return TSelf,
    // operate on TSelf, or accept TSelf.
}

public class Derived : Base<Derived>
{
    // ...
}
```

This simple `where TSelf : Base<TSelf>` constraint is the magic. It tells the compiler that `TSelf` *must* be a type that inherits from `Base<TSelf>`. Inside `Base<TSelf>`, you can now use `TSelf` as a return type for methods, or as a parameter type, effectively making the base class aware of the concrete type that is inheriting from it.

Let's illustrate this with a practical example: a robust `Result` pattern designed for asynchronous service operations, incorporating logging and demonstrating dependency injection.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

// Base CRTP Result for type-safe chaining
public abstract class BaseResult<TSuccess, TError, TSelf>
    where TSelf : BaseResult<TSuccess, TError, TSelf> // The CRTP constraint
{
    public bool IsSuccess { get; protected init; }
    public bool IsFailure => !IsSuccess;

    protected TSuccess? _successValue;
    protected TError? _errorValue;

    // Protected constructor to ensure derived classes manage their own instantiation
    protected BaseResult(TSuccess? success = default, TError? error = default, bool isSuccess = true)
    {
        IsSuccess = isSuccess;
        _successValue = success;
        _errorValue = error;
    }

    public TSuccess GetSuccessValueOrThrow() =>
        IsSuccess ? _successValue! : throw new InvalidOperationException("Cannot access success value on a failed result.");

    public TError GetErrorValueOrThrow() =>
        IsFailure ? _errorValue! : throw new InvalidOperationException("Cannot access error value on a successful result.");

    // Fluent methods returning TSelf, preserving the derived type
    public TSelf OnSuccess(Action<TSuccess> action)
    {
        if (IsSuccess)
        {
            action(_successValue!);
        }
        return (TSelf)this; // Crucially returns TSelf
    }

    public TSelf OnFailure(Action<TError> action)
    {
        if (IsFailure)
        {
            action(_errorValue!);
        }
        return (TSelf)this; // Crucially returns TSelf
    }

    public TSelf Then(Func<TSuccess, TSelf> nextOperation)
    {
        if (IsSuccess)
        {
            return nextOperation(_successValue!); // Returns a new TSelf from the next operation
        }
        return (TSelf)this;
    }

    public async Task<TSelf> ThenAsync(Func<TSuccess, Task<TSelf>> nextOperation)
    {
        if (IsSuccess)
        {
            return await nextOperation(_successValue!); // Returns a new TSelf from the next async operation
        }
        return (TSelf)this;
    }

    public TOut Match<TOut>(Func<TSuccess, TOut> onSuccess, Func<TError, TOut> onFailure) =>
        IsSuccess ? onSuccess(_successValue!) : onFailure(_errorValue!);
}

// Derived CRTP Result type with specific metadata
public class ServiceOperationResult<TSuccess, TError> : BaseResult<TSuccess, TError, ServiceOperationResult<TSuccess, TError>>
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string OperationId { get; init; } = Guid.NewGuid().ToString();

    // Protected constructor, used internally by factory methods
    protected ServiceOperationResult(TSuccess? success = default, TError? error = default, bool isSuccess = true)
        : base(success, error, isSuccess) { }

    // Public static factory methods specific to ServiceOperationResult
    // These ensure new instances are created with appropriate metadata
    public static ServiceOperationResult<TSuccess, TError> Success(TSuccess value) =>
        new(value, default, true);

    public static ServiceOperationResult<TSuccess, TError> Failure(TError error) =>
        new(default, error, false);
}

// Example domain models and errors
public class Order
{
    public int Id { get; set; }
    public string Product { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal TotalAmount { get; set; }
}

public enum OrderProcessingError
{
    InvalidOrder,
    InsufficientStock,
    PaymentFailed,
    ShippingFailure,
    Unknown
}

// Service interface
public interface IOrderService
{
    Task<ServiceOperationResult<Order, OrderProcessingError>> ProcessOrderAsync(Order order);
    Task<ServiceOperationResult<bool, OrderProcessingError>> ShipOrderAsync(int orderId);
}

// Implementation of the service, leveraging our CRTP Result
public class OrderService : IOrderService
{
    private readonly ILogger<OrderService> _logger;

    public OrderService(ILogger<OrderService> logger)
    {
        _logger = logger;
    }

    public async Task<ServiceOperationResult<Order, OrderProcessingError>> ProcessOrderAsync(Order order)
    {
        _logger.LogInformation("Attempting to process order {OrderId}", order.Id);

        // Simulate async operation and business logic
        await Task.Delay(100);

        if (order.Quantity <= 0)
        {
            _logger.LogWarning("Invalid order quantity for order {OrderId}", order.Id);
            return ServiceOperationResult<Order, OrderProcessingError>.Failure(OrderProcessingError.InvalidOrder)
                .OnFailure(err => _logger.LogError("Processing failed due to {Error}", err));
        }

        // Simulate payment failure
        if (order.TotalAmount > 1000 && new Random().Next(0, 2) == 0)
        {
            _logger.LogError("Payment failed for order {OrderId}", order.Id);
            return ServiceOperationResult<Order, OrderProcessingError>.Failure(OrderProcessingError.PaymentFailed)
                .OnFailure(err => _logger.LogError("Processing failed due to {Error}. Operation ID: {OpId}", err, ServiceOperationResult<Order, OrderProcessingError>.Failure(err).OperationId));
        }

        // Return a successful result, CRTP ensures specific properties are available
        var successResult = ServiceOperationResult<Order, OrderProcessingError>.Success(order);
        _logger.LogInformation("Order {OrderId} processed successfully. Operation ID: {OperationId}", order.Id, successResult.OperationId);
        return successResult
            .OnSuccess(o => _logger.LogDebug("Order total: {Total}", o.TotalAmount));
    }

    public async Task<ServiceOperationResult<bool, OrderProcessingError>> ShipOrderAsync(int orderId)
    {
        _logger.LogInformation("Attempting to ship order {OrderId}", orderId);
        await Task.Delay(50); // Simulate shipping logic

        if (orderId % 2 != 0) // Simulate odd order IDs failing to ship
        {
            _logger.LogError("Shipping failed for order {OrderId}", orderId);
            return ServiceOperationResult<bool, OrderProcessingError>.Failure(OrderProcessingError.ShippingFailure)
                .OnFailure(err => _logger.LogError("Shipping failed due to {Error}. Operation ID: {OpId}", err, ServiceOperationResult<bool, OrderProcessingError>.Failure(err).OperationId));
        }

        var successResult = ServiceOperationResult<bool, OrderProcessingError>.Success(true);
        _logger.LogInformation("Order {OrderId} shipped successfully. Timestamp: {Timestamp}", orderId, successResult.Timestamp);
        return successResult;
    }
}

// A consumer demonstrating the fluent API with CRTP and DI
public class OrderProcessor
{
    private readonly IOrderService _orderService;
    private readonly ILogger<OrderProcessor> _logger;

    public OrderProcessor(IOrderService orderService, ILogger<OrderProcessor> logger)
    {
        _orderService = orderService;
        _logger = logger;
    }

    public async Task ExecuteProcessingWorkflow(Order newOrder)
    {
        _logger.LogInformation("Starting workflow for order {OrderId}", newOrder.Id);

        var result = await _orderService.ProcessOrderAsync(newOrder)
            // Use ThenAsync to chain operations. CRTP ensures ServiceOperationResult is maintained.
            .ThenAsync(async processedOrder =>
            {
                _logger.LogInformation("Order {OrderId} has been processed, attempting to ship. Operation ID: {OpId}",
                                       processedOrder.Id, processedOrder.OperationId);
                // Call another service method, returning ServiceOperationResult<bool, TError>
                // We need to 'transform' this back to ServiceOperationResult<Order, TError>
                // This highlights a common pattern: if the success type changes, you need to create a new Result of the desired type.
                var shipResult = await _orderService.ShipOrderAsync(processedOrder.Id);
                return shipResult.IsSuccess
                    ? ServiceOperationResult<Order, OrderProcessingError>.Success(processedOrder) // Return original order on successful ship
                    : ServiceOperationResult<Order, OrderProcessingError>.Failure(shipResult.GetErrorValueOrThrow());
            })
            // OnSuccess/OnFailure methods also maintain the derived type
            .OnSuccess(finalOrder => _logger.LogInformation("Workflow completed successfully for order {OrderId}. Operation ID: {OpId}",
                                                            finalOrder.Id, finalOrder.OperationId))
            .OnFailure(error => _logger.LogError("Workflow failed with error: {Error}."));

        // At this point, 'result' is guaranteed to be a ServiceOperationResult<Order, OrderProcessingError>
        // Allowing direct access to its custom properties.
        if (result.IsFailure)
        {
            _logger.LogError("Final workflow state: Failure (Operation ID: {OpId}, Timestamp: {Ts})", result.OperationId, result.Timestamp);
        }
        else
        {
            _logger.LogInformation("Final workflow state: Success (Operation ID: {OpId}, Timestamp: {Ts})", result.OperationId, result.Timestamp);
        }
    }
}

// Minimal API setup for demonstration purposes
public static class DemoSetup
{
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug); // Enable Debug logs for demonstration
        });
        services.AddSingleton<IOrderService, OrderService>();
        services.AddSingleton<OrderProcessor>();
    }

    public static async Task RunDemo()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        await using var serviceProvider = services.BuildServiceProvider();

        var processor = serviceProvider.GetRequiredService<OrderProcessor>();
        var logger = serviceProvider.GetRequiredService<ILogger<DemoSetup>>();

        logger.LogInformation("\n--- Starting successful order workflow (Order 101) ---");
        await processor.ExecuteProcessingWorkflow(new Order { Id = 101, Product = "Widget", Quantity = 5, TotalAmount = 50.00M });

        logger.LogInformation("\n--- Starting failed quantity order workflow (Order 102) ---");
        await processor.ExecuteProcessingWorkflow(new Order { Id = 102, Product = "Gadget", Quantity = 0, TotalAmount = 10.00M });

        logger.LogInformation("\n--- Starting potentially failed payment order workflow (Order 103) ---");
        await processor.ExecuteProcessingWorkflow(new Order { Id = 103, Product = "SuperItem", Quantity = 2, TotalAmount = 1200.00M });

        logger.LogInformation("\n--- Starting failed shipping order workflow (Order 105) ---");
        await processor.ExecuteProcessingWorkflow(new Order { Id = 105, Product = "OddItem", Quantity = 1, TotalAmount = 10.00M });
    }

    public static async Task Main(string[] args)
    {
        await RunDemo();
    }
}
```

In this example:

1.  **`BaseResult<TSuccess, TError, TSelf>`**: This is our CRTP base. The `TSelf : BaseResult<TSuccess, TError, TSelf>` constraint is key. Its fluent methods (`OnSuccess`, `ThenAsync`) return `TSelf`, ensuring that the type of the `Result` is preserved throughout a chain of operations.
2.  **`ServiceOperationResult<TSuccess, TError>`**: This concrete derived type inherits from `BaseResult` using CRTP (`ServiceOperationResult<TSuccess, TError>`). It adds domain-specific properties like `Timestamp` and `OperationId`. Its static `Success` and `Failure` factory methods ensure that all `ServiceOperationResult` instances carry this metadata from their inception.
3.  **`OrderService`**: This service demonstrates how to use the `ServiceOperationResult`. Each operation returns a specific `ServiceOperationResult` instance, indicating either success with the desired data or a specific error. Logging is integrated at each step, leveraging the framework's built-in `ILogger`.
4.  **`OrderProcessor`**: This class shows the consumer experience. The `ThenAsync` method allows chaining of asynchronous operations, where each step either propagates the success value to the next or short-circuits on failure. Crucially, the `result` variable at the end is strongly typed as `ServiceOperationResult<Order, OrderProcessingError>`, allowing direct access to `OperationId` and `Timestamp` without any casting. This compile-time guarantee makes the code safer and easier to refactor.
5.  **Dependency Injection**: The `OrderService` and `OrderProcessor` are registered with `Microsoft.Extensions.DependencyInjection` and receive `ILogger` instances, demonstrating a standard production setup.

This design ensures that if we have a pipeline of operations, say `op1().Then(op2()).Then(op3())`, and `op1` returns a `ServiceOperationResult`, then `op2` and `op3` will also operate on and potentially return a `ServiceOperationResult`, maintaining all custom properties and type safety throughout the entire chain.

### Pitfalls and Best Practices

While CRTP is powerful, it's not a silver bullet. Like any advanced pattern, it introduces a level of complexity that needs careful consideration.

*   **Increased Cognitive Load**: The `Base<TSelf>` syntax can be intimidating for developers new to the pattern. It's not immediately obvious how `TSelf` relates to the derived type. Documenting its purpose and constraints thoroughly is essential for team maintainability.
*   **Factory Method Nuances**: Directly using `new TSelf()` inside the base class is often not feasible or desirable if derived classes require specific constructor parameters (as seen in my earlier thoughts before refining the example). The best practice is for derived classes to implement their own `new static` factory methods that correctly instantiate `TSelf` and call the appropriate base constructor, as demonstrated in `ServiceOperationResult`.
*   **When Not to Use It**: CRTP is overkill if simple runtime polymorphism via `virtual` methods suffices. If your base class doesn't need compile-time knowledge of its derived type, or if you don't need to return `TSelf` from base methods, stick to simpler inheritance. Avoid using it just because it's "advanced" – use it when its specific benefits (static polymorphism, type-safe fluent APIs, compile-time policy enforcement) are genuinely needed.
*   **Testing**: While CRTP enhances compile-time safety, it can sometimes complicate unit testing if the base class is tightly coupled to the derived type's construction. Favor composition or well-defined factory methods to maintain testability.

### Conclusion

CRTP is a sophisticated tool in the C# architect's toolkit, particularly valuable for designing highly extensible and type-safe libraries. By allowing a base class to be aware of its derived type at compile time, it enables robust fluent APIs, compile-time policy enforcement, and avoids the runtime overhead and type-safety compromises associated with traditional inheritance in certain scenarios. It's not a pattern to reach for daily, but when designing foundational library components where static polymorphism and precise type preservation are paramount, CRTP delivers a powerful and elegant solution. Understand its mechanics, appreciate its trade-offs, and deploy it where its unique benefits truly shine.
