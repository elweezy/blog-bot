---
layout: post
title: "Effective Unit Testing of Entity Framework Core DbContext with Mocks"
date: 2026-03-04 03:50:06 +0000
categories: dotnet blog
canonical_url: "https://dev.to/fazedordecodigo/como-criar-mocks-do-dbcontext-no-entity-framework-core-8-para-testes-unitarios-pc"
---

When building robust .NET applications with Entity Framework Core, ensuring the data access layer behaves as expected is paramount. We write tests. But then, the build server starts groaning, and local development cycles extend due to sluggish test suites. This usually points to one of the perennial challenges: unit testing components that interact directly with `DbContext`.

For truly isolated unit tests—the kind that run in milliseconds, provide immediate feedback, and don't touch external resources—we need to decouple our business logic from the actual database. This means effectively mocking `DbContext` and its `DbSet<T>` properties. Ignoring this often leads to a false sense of security with integration tests doubling as unit tests, or worse, fragile tests that are slow, complex to set up, and prone to environmental flakiness.

The relevance of isolated unit testing for data access layers has only grown. In a world of microservices, serverless functions, and CI/CD pipelines demanding rapid feedback, waiting minutes for tests to run is a non-starter. Modern .NET applications, often leveraging dependency injection and asynchronous programming, thrive on testability. `DbContext` in EF Core, while a powerful abstraction, can be a tricky dependency to manage in isolation due to its rich internal state and query translation capabilities. Yet, with a clear understanding of its contract and the right mocking tools, we can achieve highly effective unit tests.

### Understanding the `DbSet<T>` Contract

At its core, a `DbSet<T>` in EF Core implements `IQueryable<T>`. This interface is our primary entry point for mocking query operations. When our application code performs operations like `.Where(...).ToList()`, `.FirstOrDefaultAsync()`, or `.AnyAsync()`, it's interacting with this `IQueryable<T>` contract. The challenge arises with asynchronous methods, as `IQueryable<T>` itself is synchronous. EF Core extends this with `IAsyncEnumerable<T>` for asynchronous enumeration.

Successfully mocking `DbSet<T>` for query operations therefore involves:
1.  Providing a mock `IQueryable<T>` that returns our controlled test data.
2.  Ensuring that asynchronous extension methods like `ToListAsync()` and `FirstOrDefaultAsync()` operate correctly on this mock, typically by simulating `IAsyncEnumerable<T>` behavior.
3.  Handling non-query operations like `Add()`, `Update()`, `Remove()`, and `SaveChanges()` by verifying method calls.

The pitfall here is trying to mock *all* of `DbContext`'s internal complexities. We don't need to simulate change tracking, entity states, or complex query provider logic for most unit tests. Our goal is to test *our code* that uses `DbContext`, not `DbContext` itself.

Let's consider a practical scenario. We have a `ProductService` that retrieves and manages `Product` entities.

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Stock { get; set; }
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public virtual DbSet<Product> Products { get; set; } = default!; // Mark as default! to satisfy nullable reference types
}

public interface IProductService
{
    Task<Product?> GetProductByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<IEnumerable<Product>> GetAvailableProductsAsync(CancellationToken cancellationToken = default);
    Task<Product> AddProductAsync(Product product, CancellationToken cancellationToken = default);
    Task UpdateProductStockAsync(int id, int quantityChange, CancellationToken cancellationToken = default);
}

public class ProductService : IProductService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<ProductService> _logger;

    public ProductService(AppDbContext dbContext, ILogger<ProductService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Product?> GetProductByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Retrieving product with ID: {ProductId}", id);
        return await _dbContext.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<IEnumerable<Product>> GetAvailableProductsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Retrieving all available products.");
        return await _dbContext.Products
                               .Where(p => p.Stock > 0)
                               .ToListAsync(cancellationToken);
    }

    public async Task<Product> AddProductAsync(Product product, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Adding new product: {ProductName}", product.Name);
        _dbContext.Products.Add(product);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return product;
    }

    public async Task UpdateProductStockAsync(int id, int quantityChange, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Updating stock for product ID: {ProductId} with change: {QuantityChange}", id, quantityChange);
        var product = await _dbContext.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            _logger.LogWarning("Product with ID {ProductId} not found for stock update.", id);
            throw new InvalidOperationException($"Product with ID {id} not found.");
        }

        product.Stock += quantityChange;
        // DbContext automatically tracks changes for attached entities.
        // For unit tests, we primarily ensure SaveChanges is called.
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
```

This `ProductService` uses dependency injection for `AppDbContext` and `ILogger<ProductService>`, and performs typical EF Core operations including asynchronous queries and updates.

### Crafting Robust `DbContext` Mocks

To unit test `ProductService` effectively, we need to mock `AppDbContext` and its `DbSet<Product>`. The `DbSet<T>` mocking is the critical part, especially for asynchronous query methods.

Here's how we can set up mocks using Moq:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit; // Or NUnit, MSTest

public class ProductServiceTests
{
    // Helper to create an IAsyncEnumerable from an IEnumerable for mocking EF Core async operations
    private static IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> source)
    {
        return new AsyncEnumerable<T>(source);
    }

    private class AsyncEnumerable<T> : IAsyncEnumerable<T>
    {
        private readonly IEnumerable<T> _source;

        public AsyncEnumerable(IEnumerable<T> source) => _source = source;

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new AsyncEnumerator<T>(_source.GetEnumerator());
        }

        private class AsyncEnumerator<T> : IAsyncEnumerator<T>
        {
            private readonly IEnumerator<T> _enumerator;

            public AsyncEnumerator(IEnumerator<T> enumerator) => _enumerator = enumerator;

            public T Current => _enumerator.Current;

            public ValueTask DisposeAsync()
            {
                _enumerator.Dispose();
                return ValueTask.CompletedTask;
            }

            public ValueTask<bool> MoveNextAsync()
            {
                return ValueTask.FromResult(_enumerator.MoveNext());
            }
        }
    }

    [Fact]
    public async Task GetProductByIdAsync_ShouldReturnProduct_WhenExists()
    {
        // Arrange
        var products = new List<Product>
        {
            new Product { Id = 1, Name = "Laptop", Price = 1200m, Stock = 10 },
            new Product { Id = 2, Name = "Mouse", Price = 25m, Stock = 50 }
        }.AsQueryable();

        var mockDbSet = new Mock<DbSet<Product>>();
        // Setup IQueryable methods
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.Provider).Returns(new TestAsyncQueryProvider<Product>(products.Provider));
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.Expression).Returns(products.Expression);
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.ElementType).Returns(products.ElementType);
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.GetEnumerator()).Returns(products.GetEnumerator());

        // Setup IAsyncEnumerable methods for async queries
        mockDbSet.As<IAsyncEnumerable<Product>>().Setup(m => m.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(products).GetAsyncEnumerator(CancellationToken.None));

        var mockDbContext = new Mock<AppDbContext>(new DbContextOptions<AppDbContext>());
        mockDbContext.Setup(c => c.Products).Returns(mockDbSet.Object);

        var mockLogger = new Mock<ILogger<ProductService>>();
        var service = new ProductService(mockDbContext.Object, mockLogger.Object);

        // Act
        var result = await service.GetProductByIdAsync(1);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Laptop", result.Name);
    }

    [Fact]
    public async Task GetProductByIdAsync_ShouldReturnNull_WhenProductDoesNotExist()
    {
        // Arrange
        var products = new List<Product>
        {
            new Product { Id = 1, Name = "Laptop", Price = 1200m, Stock = 10 }
        }.AsQueryable();

        var mockDbSet = new Mock<DbSet<Product>>();
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.Provider).Returns(new TestAsyncQueryProvider<Product>(products.Provider));
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.Expression).Returns(products.Expression);
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.ElementType).Returns(products.ElementType);
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.GetEnumerator()).Returns(products.GetEnumerator());

        mockDbSet.As<IAsyncEnumerable<Product>>().Setup(m => m.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(products).GetAsyncEnumerator(CancellationToken.None));

        var mockDbContext = new Mock<AppDbContext>(new DbContextOptions<AppDbContext>());
        mockDbContext.Setup(c => c.Products).Returns(mockDbSet.Object);

        var mockLogger = new Mock<ILogger<ProductService>>();
        var service = new ProductService(mockDbContext.Object, mockLogger.Object);

        // Act
        var result = await service.GetProductByIdAsync(99);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAvailableProductsAsync_ShouldReturnOnlyInStockProducts()
    {
        // Arrange
        var products = new List<Product>
        {
            new Product { Id = 1, Name = "Laptop", Price = 1200m, Stock = 10 },
            new Product { Id = 2, Name = "Mouse", Price = 25m, Stock = 0 }, // Out of stock
            new Product { Id = 3, Name = "Keyboard", Price = 75m, Stock = 5 }
        }.AsQueryable();

        var mockDbSet = new Mock<DbSet<Product>>();
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.Provider).Returns(new TestAsyncQueryProvider<Product>(products.Provider));
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.Expression).Returns(products.Expression);
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.ElementType).Returns(products.ElementType);
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.GetEnumerator()).Returns(products.GetEnumerator());

        mockDbSet.As<IAsyncEnumerable<Product>>().Setup(m => m.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(products).GetAsyncEnumerator(CancellationToken.None));

        var mockDbContext = new Mock<AppDbContext>(new DbContextOptions<AppDbContext>());
        mockDbContext.Setup(c => c.Products).Returns(mockDbSet.Object);

        var mockLogger = new Mock<ILogger<ProductService>>();
        var service = new ProductService(mockDbContext.Object, mockLogger.Object);

        // Act
        var result = (await service.GetAvailableProductsAsync()).ToList();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.Name == "Laptop");
        Assert.Contains(result, p => p.Name == "Keyboard");
        Assert.DoesNotContain(result, p => p.Name == "Mouse");
    }

    [Fact]
    public async Task AddProductAsync_ShouldAddProductAndSaveChanges()
    {
        // Arrange
        var newProduct = new Product { Name = "Monitor", Price = 300m, Stock = 20 };
        var mockDbSet = new Mock<DbSet<Product>>();
        
        // Setup Add method, if it was directly verified
        // mockDbSet.Setup(m => m.Add(It.IsAny<Product>())).Callback<Product>(p => { /* Add to a local list if needed for further queries in the same test */ });

        var mockDbContext = new Mock<AppDbContext>(new DbContextOptions<AppDbContext>());
        mockDbContext.Setup(c => c.Products).Returns(mockDbSet.Object);
        mockDbContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1); // Simulate 1 entity saved

        var mockLogger = new Mock<ILogger<ProductService>>();
        var service = new ProductService(mockDbContext.Object, mockLogger.Object);

        // Act
        var addedProduct = await service.AddProductAsync(newProduct);

        // Assert
        mockDbSet.Verify(m => m.Add(It.Is<Product>(p => p.Name == "Monitor")), Times.Once);
        mockDbContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(newProduct, addedProduct); // Ensure the returned product is the one passed in
    }

    [Fact]
    public async Task UpdateProductStockAsync_ShouldUpdateStockAndSaveChanges_WhenProductExists()
    {
        // Arrange
        var productToUpdate = new Product { Id = 1, Name = "Laptop", Price = 1200m, Stock = 10 };
        var products = new List<Product> { productToUpdate }.AsQueryable();

        var mockDbSet = new Mock<DbSet<Product>>();
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.Provider).Returns(new TestAsyncQueryProvider<Product>(products.Provider));
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.Expression).Returns(products.Expression);
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.ElementType).Returns(products.ElementType);
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.GetEnumerator()).Returns(products.GetEnumerator());
        mockDbSet.As<IAsyncEnumerable<Product>>().Setup(m => m.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(products).GetAsyncEnumerator(CancellationToken.None));

        var mockDbContext = new Mock<AppDbContext>(new DbContextOptions<AppDbContext>());
        mockDbContext.Setup(c => c.Products).Returns(mockDbSet.Object);
        mockDbContext.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var mockLogger = new Mock<ILogger<ProductService>>();
        var service = new ProductService(mockDbContext.Object, mockLogger.Object);
        
        // Act
        await service.UpdateProductStockAsync(1, 5); // Add 5 to stock

        // Assert
        Assert.Equal(15, productToUpdate.Stock); // Verify the entity state was updated
        mockDbContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateProductStockAsync_ShouldThrowException_WhenProductDoesNotExist()
    {
        // Arrange
        var products = new List<Product>().AsQueryable(); // No products in the mock DB

        var mockDbSet = new Mock<DbSet<Product>>();
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.Provider).Returns(new TestAsyncQueryProvider<Product>(products.Provider));
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.Expression).Returns(products.Expression);
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.ElementType).Returns(products.ElementType);
        mockDbSet.As<IQueryable<Product>>().Setup(m => m.GetEnumerator()).Returns(products.GetEnumerator());
        mockDbSet.As<IAsyncEnumerable<Product>>().Setup(m => m.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(products).GetAsyncEnumerator(CancellationToken.None));

        var mockDbContext = new Mock<AppDbContext>(new DbContextOptions<AppDbContext>());
        mockDbContext.Setup(c => c.Products).Returns(mockDbSet.Object);

        var mockLogger = new Mock<ILogger<ProductService>>();
        var service = new ProductService(mockDbContext.Object, mockLogger.Object);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateProductStockAsync(99, 5));
        mockDbContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never); // Ensure SaveChanges was NOT called
    }

    // A minimal IAsyncQueryProvider for unit testing DbSet async methods.
    // This allows EF Core's async extension methods (ToListAsync, FirstOrDefaultAsync etc.)
    // to work directly on our in-memory IQueryable, without needing a real database.
    private class TestAsyncQueryProvider<TEntity> : IAsyncQueryProvider
    {
        private readonly IQueryProvider _inner;

        internal TestAsyncQueryProvider(IQueryProvider inner)
        {
            _inner = inner;
        }

        public IQueryable CreateQuery(Expression expression)
        {
            return new TestAsyncEnumerable<TEntity>(expression);
        }

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        {
            return new TestAsyncEnumerable<TElement>(expression);
        }

        public object? Execute(Expression expression)
        {
            return _inner.Execute(expression);
        }

        public TResult Execute<TResult>(Expression expression)
        {
            return _inner.Execute<TResult>(expression);
        }

        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            var expectedResultType = typeof(TResult).GetGenericArguments()[0];
            var enumerationResultType = typeof(IAsyncEnumerable<>).MakeGenericType(expectedResultType);

            // Execute the query using the synchronous provider
            var result = _inner.Execute<TResult>(expression);

            // Special handling for results that are expected to be awaited
            // e.g., FirstOrDefaultAsync, ToListAsync
            if (result is IQueryable queryable)
            {
                // This is a simplified approach. For more complex scenarios,
                // you might need to use EF Core's InMemory provider's query translation.
                // But for basic filtering/projection, direct execution is often enough.
                var asEnumerable = ((IEnumerable)queryable).Cast<TEntity>().AsQueryable();

                // If the return type is a Task<List<T>>, convert to list and wrap in Task.
                if (typeof(TResult).IsGenericType && typeof(TResult).GetGenericTypeDefinition() == typeof(Task<>) &&
                    expectedResultType.IsGenericType && expectedResultType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var listType = typeof(List<>).MakeGenericType(expectedResultType.GetGenericArguments()[0]);
                    var list = Activator.CreateInstance(listType, asEnumerable.ToList());
                    return (TResult)(object)Task.FromResult(list);
                }
                
                // If the return type is Task<T>, convert to single item and wrap in Task.
                if (typeof(TResult).IsGenericType && typeof(TResult).GetGenericTypeDefinition() == typeof(Task<>) &&
                    !expectedResultType.IsGenericType)
                {
                    var item = asEnumerable.FirstOrDefault(); // This executes the query against the in-memory data
                    return (TResult)(object)Task.FromResult(item);
                }
            }
            // For other scenarios, like .CountAsync(), .AnyAsync(), the synchronous result might already be sufficient
            // as the expression trees are evaluated against the in-memory source.
            return result;
        }
    }

    // A minimal IQueryable for unit testing DbSet async methods.
    // This allows LINQ expressions to be built against our in-memory data source.
    private class TestAsyncEnumerable<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
    {
        public TestAsyncEnumerable(IEnumerable<T> enumerable)
            : base(enumerable)
        { }

        public TestAsyncEnumerable(Expression expression)
            : base(expression)
        { }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new AsyncEnumerator<T>(this.AsEnumerable().GetEnumerator());
        }
    }
}
```

#### Why This Approach?

1.  **`TestAsyncQueryProvider`**: The core challenge with EF Core `DbSet<T>` is that its asynchronous extension methods (like `ToListAsync`, `FirstOrDefaultAsync`) rely on `IAsyncQueryProvider`. A standard `IQueryable<T>` from a simple `List<T>.AsQueryable()` doesn't implement this. My `TestAsyncQueryProvider` bridges this gap. It wraps the synchronous `IQueryProvider` of an in-memory `IEnumerable<T>` and, importantly, implements `ExecuteAsync` to correctly translate the asynchronous calls into synchronous executions against our mock data. This allows our LINQ queries to be evaluated against the mock data *in memory*, just as they would against a real database, but without the I/O overhead.
2.  **`ToAsyncEnumerable` Helper**: This utility provides a simple `IAsyncEnumerable<T>` implementation that wraps an `IEnumerable<T>`. This is crucial for mocking the `GetAsyncEnumerator` method of `DbSet.As<IAsyncEnumerable<T>>()`, ensuring that `.ToListAsync()` and similar methods can iterate over our mock data.
3.  **Mocking `DbSet.As<IQueryable<T>>()`**: We explicitly tell Moq to treat `mockDbSet` as an `IQueryable<T>` and set up its `Provider`, `Expression`, `ElementType`, and `GetEnumerator` properties to point to our in-memory `IQueryable<T>` (created from `products.AsQueryable()`). This is how synchronous LINQ queries are supported.
4.  **Mocking `DbSet.As<IAsyncEnumerable<T>>()`**: Similarly, we mock `GetAsyncEnumerator` to return our `ToAsyncEnumerable` helper. This completes the picture for `async` LINQ methods.
5.  **Mocking `Add()`, `SaveChanges()`**: For write operations, we don't need to simulate complex database behavior. For `Add()`, we usually just verify that the method was called with the correct entity. For `SaveChangesAsync()`, we just ensure it was invoked and return a successful `Task<int>` (e.g., `Task.FromResult(1)` to indicate one entity was saved). The actual change tracking and persistence logic is EF Core's responsibility, not ours to unit test.
6.  **`ILogger` Mocking**: Standard practice. We just need an `ILogger` instance to pass to our service; usually, we don't need to verify specific log messages in unit tests unless logging behavior is a critical part of the tested logic.

### Pitfalls and Best Practices

*   **Don't over-mock `DbContext`'s internal state**: Resist the urge to simulate `ChangeTracker` behavior, complex query translation, or transaction management. These are EF Core's responsibilities. If your unit test needs to interact with these deeply, it's likely creeping into integration testing territory.
*   **Focus on the `IQueryable<T>` contract**: For queries, this is the most important interface. Ensure your mock `DbSet` provides a functional `IQueryable<T>` and `IAsyncEnumerable<T>` over your controlled test data.
*   **Use an in-memory database for integration tests, not unit tests**: The `Microsoft.EntityFrameworkCore.InMemory` provider is excellent for *integration tests* because it provides a realistic, albeit in-memory, EF Core environment. It handles change tracking, concurrency, and schema migrations more accurately than hand-rolled mocks. However, it's slower than pure mocks and still ties you to EF Core's specific behaviors. For lightning-fast, isolated unit tests, mocks are superior.
*   **Keep data access concerns isolated**: The `ProductService` above directly uses `DbContext`. For larger applications, encapsulating `DbSet` interactions within a dedicated repository layer (e.g., `IProductRepository`) can further simplify mocking, as your service would then depend on `IProductRepository` which is easier to mock than `DbSet<T>`. However, even with a repository, the underlying repository implementation still needs to deal with `DbSet<T>` and would use the same mocking techniques for its own unit tests.
*   **Test error conditions**: Always include tests for scenarios where entities are not found, or where expected operations might fail (e.g., `SaveChanges` throwing an exception, though less common for unit tests of business logic).
*   **`DbContextOptions` during mocking**: When creating `Mock<AppDbContext>`, passing `new DbContextOptions<AppDbContext>()` is generally sufficient. Your unit tests don't need to configure a real database provider.

### Conclusion

Effective unit testing of `DbContext` interactions in Entity Framework Core requires a deliberate approach to mocking. By focusing on the `IQueryable<T>` and `IAsyncEnumerable<T>` interfaces of `DbSet<T>`, and providing a `TestAsyncQueryProvider` that correctly translates asynchronous LINQ queries to in-memory operations, we can build highly reliable and lightning-fast unit tests. This investment in testability pays dividends in developer productivity, application robustness, and the agility of our CI/CD pipelines, ultimately contributing to a more maintainable and confident codebase.
