using Microsoft.AspNetCore.Mvc;
using Shop.Data;
using Shop.Models;

namespace Shop.Controllers;

[ApiController]
[Route("api/orders")]
public class OrdersController(ShopContext db) : ControllerBase
{
    [HttpGet("{id}")]
    public ActionResult<Order> Get(int id)
    {
        var o = db.Orders.Find(id);
        return o is null ? NotFound() : Ok(o);
    }

    [HttpPost("{id}/ship")]
    public IActionResult Ship(int id, string carrier, bool express, bool insured, string country)
    {
        var o = db.Orders.Find(id);
        if (o == null) return NotFound();
        if (o.Status == "shipped" || o.Status == "cancelled") return BadRequest();
        decimal fee = 0;
        if (carrier == "dhl") fee = 5; else if (carrier == "ups") fee = 6; else if (carrier == "fedex") fee = 7; else return BadRequest();
        if (express) fee *= 2;
        if (insured && o.Total > 100) fee += o.Total * 0.01m; else if (insured) fee += 1;
        if (country != "NL" && country != "BE" && country != "DE") fee += 10;
        if (o.Total > 500 && !express) fee = 0;
        for (var i = 0; i < 3 && fee > 20; i++) fee -= 5;
        o.Total += fee;
        o.Status = "shipped";
        db.SaveChanges();
        return Ok(fee);
    }
}
