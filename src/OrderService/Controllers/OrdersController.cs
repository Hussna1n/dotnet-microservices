using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MassTransit;
using OrderService.Data;
using OrderService.Models;

namespace OrderService.Controllers;

[ApiController]
[Route("api/orders")]
[Authorize]
public class OrdersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPublishEndpoint _publish;

    public OrdersController(AppDbContext db, IPublishEndpoint publish)
    {
        _db = db;
        _publish = publish;
    }

    public record CreateOrderDto(string ShippingAddress, List<OrderItemDto> Items);
    public record OrderItemDto(int ProductId, string ProductName, decimal UnitPrice, int Quantity);

    [HttpGet]
    public async Task<IActionResult> GetMyOrders([FromQuery] int page = 1, [FromQuery] int limit = 10)
    {
        var userId = int.Parse(User.FindFirst("sub")!.Value);
        var q = _db.Orders.Include(o => o.Items).Where(o => o.UserId == userId);
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(o => o.CreatedAt)
                           .Skip((page - 1) * limit).Take(limit).ToListAsync();
        return Ok(new { total, page, items });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var userId = int.Parse(User.FindFirst("sub")!.Value);
        var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;

        var order = await _db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);
        if (order == null) return NotFound();
        if (order.UserId != userId && role != "admin") return Forbid();

        return Ok(order);
    }

    [HttpPost]
    public async Task<IActionResult> PlaceOrder([FromBody] CreateOrderDto dto)
    {
        var userId = int.Parse(User.FindFirst("sub")!.Value);

        var order = new Order
        {
            UserId = userId,
            ShippingAddress = dto.ShippingAddress,
            TotalAmount = dto.Items.Sum(i => i.UnitPrice * i.Quantity),
            Items = dto.Items.Select(i => new OrderItem
            {
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                UnitPrice = i.UnitPrice,
                Quantity = i.Quantity
            }).ToList()
        };

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        await _publish.Publish(new OrderPlacedEvent(
            order.Id,
            userId,
            order.TotalAmount,
            dto.Items.Select(i => i.ProductId).ToList()
        ));

        return CreatedAtAction(nameof(GetById), new { id = order.Id }, order);
    }

    [HttpPatch("{id}/status")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] OrderStatus newStatus)
    {
        var order = await _db.Orders.FindAsync(id);
        if (order == null) return NotFound();

        var oldStatus = order.Status;
        order.Status = newStatus;
        order.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _publish.Publish(new OrderStatusChangedEvent(order.Id, oldStatus, newStatus));

        return Ok(new { order.Id, oldStatus, newStatus = order.Status });
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        var userId = int.Parse(User.FindFirst("sub")!.Value);
        var order = await _db.Orders.FindAsync(id);
        if (order == null) return NotFound();
        if (order.UserId != userId) return Forbid();
        if (order.Status >= OrderStatus.Shipped)
            return BadRequest(new { message = "Cannot cancel an order that has already shipped" });

        var old = order.Status;
        order.Status = OrderStatus.Cancelled;
        order.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _publish.Publish(new OrderStatusChangedEvent(order.Id, old, OrderStatus.Cancelled));

        return Ok(new { order.Id, order.Status });
    }

    [HttpGet("admin/all")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> GetAll([FromQuery] OrderStatus? status, [FromQuery] int page = 1, [FromQuery] int limit = 20)
    {
        var q = _db.Orders.Include(o => o.Items).AsQueryable();
        if (status.HasValue) q = q.Where(o => o.Status == status.Value);
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(o => o.CreatedAt)
                           .Skip((page - 1) * limit).Take(limit).ToListAsync();
        return Ok(new { total, page, items });
    }
}
