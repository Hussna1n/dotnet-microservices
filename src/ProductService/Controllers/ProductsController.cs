using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MassTransit;
using ProductService.Data;
using ProductService.Models;

namespace ProductService.Controllers;

[ApiController]
[Route("api/products")]
[Authorize]
public class ProductsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPublishEndpoint _publish;

    public ProductsController(AppDbContext db, IPublishEndpoint publish)
    {
        _db = db;
        _publish = publish;
    }

    public record CreateProductDto(string Name, string Description, decimal Price, int Stock, string Category, string ImageUrl);
    public record UpdateProductDto(string? Name, string? Description, decimal? Price, int? Stock, bool? IsActive);
    public record AdjustStockDto(int Delta, string Reason);

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? category,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20)
    {
        var q = _db.Products.Where(p => p.IsActive);
        if (!string.IsNullOrEmpty(category)) q = q.Where(p => p.Category == category);
        if (!string.IsNullOrEmpty(search)) q = q.Where(p => p.Name.Contains(search) || p.Description.Contains(search));

        var total = await q.CountAsync();
        var items = await q.OrderByDescending(p => p.CreatedAt)
                           .Skip((page - 1) * limit).Take(limit).ToListAsync();
        return Ok(new { total, page, limit, items });
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(int id)
    {
        var p = await _db.Products.FindAsync(id);
        return p == null ? NotFound() : Ok(p);
    }

    [HttpPost]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Create([FromBody] CreateProductDto dto)
    {
        var userId = int.Parse(User.FindFirst("sub")!.Value);
        var product = new Product
        {
            Name = dto.Name,
            Description = dto.Description,
            Price = dto.Price,
            Stock = dto.Stock,
            Category = dto.Category,
            ImageUrl = dto.ImageUrl,
            CreatedBy = userId
        };
        _db.Products.Add(product);
        await _db.SaveChangesAsync();

        await _publish.Publish(new ProductCreatedEvent(product.Id, product.Name, product.Price, product.Stock));

        return CreatedAtAction(nameof(GetById), new { id = product.Id }, product);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateProductDto dto)
    {
        var product = await _db.Products.FindAsync(id);
        if (product == null) return NotFound();

        if (dto.Name != null) product.Name = dto.Name;
        if (dto.Description != null) product.Description = dto.Description;
        if (dto.Price.HasValue) product.Price = dto.Price.Value;
        if (dto.Stock.HasValue) product.Stock = dto.Stock.Value;
        if (dto.IsActive.HasValue) product.IsActive = dto.IsActive.Value;
        product.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(product);
    }

    [HttpPatch("{id}/stock")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> AdjustStock(int id, [FromBody] AdjustStockDto dto)
    {
        var product = await _db.Products.FindAsync(id);
        if (product == null) return NotFound();

        product.Stock = Math.Max(0, product.Stock + dto.Delta);
        product.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _publish.Publish(new StockUpdatedEvent(product.Id, product.Stock));

        return Ok(new { product.Id, product.Stock, dto.Reason });
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var product = await _db.Products.FindAsync(id);
        if (product == null) return NotFound();
        product.IsActive = false;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("categories")]
    [AllowAnonymous]
    public async Task<IActionResult> GetCategories()
    {
        var cats = await _db.Products.Where(p => p.IsActive)
            .Select(p => p.Category).Distinct().ToListAsync();
        return Ok(cats);
    }
}
