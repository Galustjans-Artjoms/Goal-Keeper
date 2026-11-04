using Dashboard.Api.Transactions;
using Microsoft.AspNetCore.Mvc;

namespace Dashboard.Api.Controllers;

[ApiController]
[Route("api/transactions")]
public class TransactionsController : ControllerBase
{
    private readonly TransactionService _service;

    public TransactionsController(TransactionService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<PagedTransactions> List(
        [FromQuery] TransactionFilter filter,
        [FromQuery] int page = 0,
        [FromQuery] int size = 20,
        [FromQuery] string sort = "date",
        [FromQuery] string dir = "desc",
        CancellationToken cancellationToken = default)
    {
        var safeSort = sort is "id" or "date" or "amount" or "category" ? sort : "date";
        return await _service.FindAsync(filter, Math.Max(page, 0), Math.Max(size, 1), safeSort, dir, cancellationToken);
    }

    [HttpGet("{id:long}")]
    public async Task<Data.Transaction> Get(long id, CancellationToken cancellationToken)
    {
        return await _service.GetAsync(id, cancellationToken);
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<Data.Transaction>> Create(TransactionRequest request, CancellationToken cancellationToken)
    {
        var created = await _service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:long}")]
    public async Task<Data.Transaction> Update(long id, TransactionRequest request, CancellationToken cancellationToken)
    {
        return await _service.UpdateAsync(id, request, cancellationToken);
    }

    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
